using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Serilog;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v19: Klavye kısayolları yönetimi
    /// Global ve pencere bazlı kısayolları yönetir
    /// </summary>
    public sealed class KeyboardShortcuts : IDisposable
    {
        #region Singleton

        private static readonly Lazy<KeyboardShortcuts> _instance =
            new(() => new KeyboardShortcuts());

        public static KeyboardShortcuts Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly Dictionary<string, ShortcutBinding> _shortcuts = new();
        private readonly Dictionary<string, string> _shortcutDescriptions = new();
        private Window? _mainWindow;
        private bool _isEnabled = true;

        #endregion

        #region Events

        /// <summary>
        /// Kısayol tetiklendiğinde
        /// </summary>
        public event EventHandler<ShortcutTriggeredEventArgs>? OnShortcutTriggered;

        #endregion

        #region Default Shortcuts

        /// <summary>
        /// Varsayılan kısayolları kaydet
        /// </summary>
        public void RegisterDefaults()
        {
            // Yayın kontrolü
            Register("StartStream", new KeyGesture(Key.F5), "Yayını Başlat");
            Register("StopStream", new KeyGesture(Key.F6), "Yayını Durdur");
            Register("ToggleStream", new KeyGesture(Key.F5, ModifierKeys.Shift), "Yayını Aç/Kapat");

            // Kayıt
            Register("StartRecording", new KeyGesture(Key.F7), "Kaydı Başlat");
            Register("StopRecording", new KeyGesture(Key.F8), "Kaydı Durdur");
            Register("ToggleRecording", new KeyGesture(Key.F7, ModifierKeys.Shift), "Kaydı Aç/Kapat");

            // Ses kontrolü
            Register("MuteMicrophone", new KeyGesture(Key.M, ModifierKeys.Control), "Mikrofonu Kapat");
            Register("MuteDesktop", new KeyGesture(Key.M, ModifierKeys.Control | ModifierKeys.Shift), "Masaüstü Sesini Kapat");

            // Navigasyon
            Register("TabControl", new KeyGesture(Key.D1, ModifierKeys.Control), "Yayın Paneli");
            Register("TabPreview", new KeyGesture(Key.D2, ModifierKeys.Control), "Önizleme");
            Register("TabPlatforms", new KeyGesture(Key.D3, ModifierKeys.Control), "Platformlar");
            Register("TabChat", new KeyGesture(Key.D4, ModifierKeys.Control), "Sohbet");
            Register("TabSettings", new KeyGesture(Key.D5, ModifierKeys.Control), "Ayarlar");
            Register("TabLicense", new KeyGesture(Key.D6, ModifierKeys.Control), "Lisans");

            // Overlay
            Register("ToggleOverlay", new KeyGesture(Key.O, ModifierKeys.Control), "Overlay Aç/Kapat");
            Register("ToggleChatOverlay", new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Shift), "Chat Overlay Aç/Kapat");

            // Genel
            Register("ToggleFullscreen", new KeyGesture(Key.F11), "Tam Ekran");
            Register("OpenSettings", new KeyGesture(Key.OemComma, ModifierKeys.Control), "Ayarları Aç");
            Register("RefreshDevices", new KeyGesture(Key.R, ModifierKeys.Control), "Cihazları Yenile");
            Register("ShowShortcuts", new KeyGesture(Key.OemQuestion, ModifierKeys.Control | ModifierKeys.Shift), "Kısayolları Göster");

            Log.Information("[Shortcuts] {Count} varsayılan kısayol kaydedildi", _shortcuts.Count);
        }

        #endregion

        #region Registration

        /// <summary>
        /// Kısayol kaydet
        /// </summary>
        public void Register(string id, KeyGesture gesture, string description)
        {
            if (_shortcuts.ContainsKey(id))
            {
                Log.Warning("[Shortcuts] Kısayol zaten var, güncelleniyor: {Id}", id);
            }

            _shortcuts[id] = new ShortcutBinding
            {
                Id = id,
                Gesture = gesture,
                Description = description
            };

            _shortcutDescriptions[id] = description;

            Log.Debug("[Shortcuts] Kayıt: {Id} = {Gesture}", id, FormatGesture(gesture));
        }

        /// <summary>
        /// Kısayol kaldır
        /// </summary>
        public void Unregister(string id)
        {
            _shortcuts.Remove(id);
            _shortcutDescriptions.Remove(id);
        }

        /// <summary>
        /// Kısayolu güncelle
        /// </summary>
        public void UpdateGesture(string id, KeyGesture newGesture)
        {
            if (_shortcuts.TryGetValue(id, out var binding))
            {
                binding.Gesture = newGesture;
                Log.Information("[Shortcuts] Güncellendi: {Id} = {Gesture}", id, FormatGesture(newGesture));
            }
        }

        #endregion

        #region Binding

        /// <summary>
        /// Ana pencereye bağla
        /// </summary>
        public void BindToWindow(Window window)
        {
            _mainWindow = window;
            window.PreviewKeyDown += OnPreviewKeyDown;

            // InputBindings ekle (WPF native desteği için)
            foreach (var kvp in _shortcuts)
            {
                var binding = new KeyBinding
                {
                    Key = kvp.Value.Gesture.Key,
                    Modifiers = kvp.Value.Gesture.Modifiers,
                    Command = new RelayCommand(_ => TriggerShortcut(kvp.Key))
                };
                window.InputBindings.Add(binding);
            }

            Log.Information("[Shortcuts] Pencereye bağlandı: {Window}", window.GetType().Name);
        }

        /// <summary>
        /// Pencereden ayır
        /// </summary>
        public void UnbindFromWindow(Window window)
        {
            window.PreviewKeyDown -= OnPreviewKeyDown;
            window.InputBindings.Clear();

            if (_mainWindow == window)
                _mainWindow = null;
        }

        #endregion

        #region Event Handling

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isEnabled) return;

            // Textbox içindeyken bazı kısayolları devre dışı bırak
            if (e.OriginalSource is System.Windows.Controls.TextBox && !HasModifiers(e))
                return;

            foreach (var kvp in _shortcuts)
            {
                if (MatchesGesture(e, kvp.Value.Gesture))
                {
                    TriggerShortcut(kvp.Key);
                    e.Handled = true;
                    return;
                }
            }
        }

        private bool MatchesGesture(KeyEventArgs e, KeyGesture gesture)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            return key == gesture.Key &&
                   Keyboard.Modifiers == gesture.Modifiers;
        }

        private bool HasModifiers(KeyEventArgs e)
        {
            return Keyboard.Modifiers != ModifierKeys.None;
        }

        private void TriggerShortcut(string id)
        {
            if (!_shortcuts.TryGetValue(id, out var binding))
                return;

            Log.Debug("[Shortcuts] Tetiklendi: {Id}", id);

            OnShortcutTriggered?.Invoke(this, new ShortcutTriggeredEventArgs
            {
                ShortcutId = id,
                Description = binding.Description
            });
        }

        #endregion

        #region Action Binding

        /// <summary>
        /// Kısayola action bağla
        /// </summary>
        public void BindAction(string id, Action action)
        {
            if (_shortcuts.TryGetValue(id, out var binding))
            {
                binding.Action = action;
            }
        }

        /// <summary>
        /// Kısayolu manuel tetikle
        /// </summary>
        public void Execute(string id)
        {
            if (_shortcuts.TryGetValue(id, out var binding))
            {
                binding.Action?.Invoke();
                TriggerShortcut(id);
            }
        }

        #endregion

        #region Enable/Disable

        /// <summary>
        /// Kısayolları etkinleştir
        /// </summary>
        public void Enable()
        {
            _isEnabled = true;
            Log.Debug("[Shortcuts] Etkinleştirildi");
        }

        /// <summary>
        /// Kısayolları devre dışı bırak
        /// </summary>
        public void Disable()
        {
            _isEnabled = false;
            Log.Debug("[Shortcuts] Devre dışı");
        }

        /// <summary>
        /// Geçici olarak devre dışı bırak
        /// </summary>
        public IDisposable SuspendTemporarily()
        {
            var wasEnabled = _isEnabled;
            _isEnabled = false;

            return new DisposableAction(() => _isEnabled = wasEnabled);
        }

        #endregion

        #region Query

        /// <summary>
        /// Kısayol var mı?
        /// </summary>
        public bool HasShortcut(string id) => _shortcuts.ContainsKey(id);

        /// <summary>
        /// Kısayol bilgisini al
        /// </summary>
        public ShortcutBinding? GetShortcut(string id)
        {
            return _shortcuts.TryGetValue(id, out var binding) ? binding : null;
        }

        /// <summary>
        /// Tüm kısayolları al
        /// </summary>
        public IEnumerable<ShortcutBinding> GetAllShortcuts()
        {
            return _shortcuts.Values;
        }

        /// <summary>
        /// Kısayolları kategoriye göre grupla
        /// </summary>
        public Dictionary<string, List<ShortcutBinding>> GetGroupedShortcuts()
        {
            var groups = new Dictionary<string, List<ShortcutBinding>>
            {
                ["Yayın"] = new(),
                ["Navigasyon"] = new(),
                ["Overlay"] = new(),
                ["Genel"] = new()
            };

            foreach (var binding in _shortcuts.Values)
            {
                var category = binding.Id switch
                {
                    _ when binding.Id.Contains("Stream") || binding.Id.Contains("Recording") || binding.Id.Contains("Mute") => "Yayın",
                    _ when binding.Id.StartsWith("Tab") => "Navigasyon",
                    _ when binding.Id.Contains("Overlay") => "Overlay",
                    _ => "Genel"
                };

                groups[category].Add(binding);
            }

            return groups;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// KeyGesture'ı okunabilir formata çevir
        /// </summary>
        public static string FormatGesture(KeyGesture gesture)
        {
            var parts = new List<string>();

            if (gesture.Modifiers.HasFlag(ModifierKeys.Control))
                parts.Add("Ctrl");
            if (gesture.Modifiers.HasFlag(ModifierKeys.Shift))
                parts.Add("Shift");
            if (gesture.Modifiers.HasFlag(ModifierKeys.Alt))
                parts.Add("Alt");

            var keyName = gesture.Key switch
            {
                Key.OemComma => ",",
                Key.OemQuestion => "?",
                Key.D1 => "1",
                Key.D2 => "2",
                Key.D3 => "3",
                Key.D4 => "4",
                Key.D5 => "5",
                Key.D6 => "6",
                _ => gesture.Key.ToString()
            };

            parts.Add(keyName);
            return string.Join("+", parts);
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_mainWindow != null)
            {
                UnbindFromWindow(_mainWindow);
            }

            _shortcuts.Clear();
            OnShortcutTriggered = null;
        }

        #endregion
    }

    #region Types

    public class ShortcutBinding
    {
        public string Id { get; set; } = "";
        public KeyGesture Gesture { get; set; } = null!;
        public string Description { get; set; } = "";
        public Action? Action { get; set; }

        public string FormattedGesture => KeyboardShortcuts.FormatGesture(Gesture);
    }

    public class ShortcutTriggeredEventArgs : EventArgs
    {
        public string ShortcutId { get; init; } = "";
        public string Description { get; init; } = "";
    }

    internal class DisposableAction : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;

        public DisposableAction(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _action();
        }
    }

    #endregion
}
