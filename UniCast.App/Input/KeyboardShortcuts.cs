using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Serilog;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UniCast.App.Input
{
    /// <summary>
    /// DÜZELTME v19: Klavye kısayolları yönetimi
    /// DÜZELTME v57: Modifier tuşları tek başına basıldığında hata düzeltildi
    /// Global ve yerel kısayol tuşları desteği
    /// </summary>
    public sealed class KeyboardShortcutManager
    {
        #region Singleton

        private static readonly Lazy<KeyboardShortcutManager> _instance =
            new(() => new KeyboardShortcutManager());

        public static KeyboardShortcutManager Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly Dictionary<string, ShortcutBinding> _shortcuts = new();
        private readonly Dictionary<KeyGesture, string> _gestureMap = new();
        private Window? _mainWindow;
        private bool _isEnabled = true;

        #endregion

        #region Events

        public event EventHandler<ShortcutTriggeredEventArgs>? OnShortcutTriggered;

        #endregion

        #region Constructor

        private KeyboardShortcutManager()
        {
            RegisterDefaultShortcuts();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Ana pencereyi kaydet ve kısayolları aktif et
        /// </summary>
        public void Initialize(Window mainWindow)
        {
            _mainWindow = mainWindow;
            _mainWindow.PreviewKeyDown += OnPreviewKeyDown;

            // Input bindings ekle
            foreach (var shortcut in _shortcuts.Values)
            {
                var binding = new KeyBinding(
                    new RelayShortcutCommand(() => ExecuteShortcut(shortcut.Id)),
                    shortcut.Gesture);

                _mainWindow.InputBindings.Add(binding);
            }

            Log.Information("[Shortcuts] {Count} kısayol kaydedildi", _shortcuts.Count);
        }

        /// <summary>
        /// Kısayol ekle veya güncelle
        /// </summary>
        public void RegisterShortcut(string id, Key key, ModifierKeys modifiers, Action action, string description)
        {
            var gesture = new KeyGesture(key, modifiers);

            var binding = new ShortcutBinding
            {
                Id = id,
                Gesture = gesture,
                Action = action,
                Description = description,
                IsEnabled = true
            };

            _shortcuts[id] = binding;
            _gestureMap[gesture] = id;

            Log.Debug("[Shortcuts] Kısayol kaydedildi: {Id} = {Gesture}", id, gesture.DisplayString);
        }

        /// <summary>
        /// Kısayolu kaldır
        /// </summary>
        public void UnregisterShortcut(string id)
        {
            if (_shortcuts.TryGetValue(id, out var binding))
            {
                _gestureMap.Remove(binding.Gesture);
                _shortcuts.Remove(id);
            }
        }

        /// <summary>
        /// Kısayolları etkinleştir/devre dışı bırak
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            Log.Debug("[Shortcuts] Enabled: {Enabled}", enabled);
        }

        /// <summary>
        /// Belirli bir kısayolu etkinleştir/devre dışı bırak
        /// </summary>
        public void SetShortcutEnabled(string id, bool enabled)
        {
            if (_shortcuts.TryGetValue(id, out var binding))
            {
                binding.IsEnabled = enabled;
            }
        }

        /// <summary>
        /// Tüm kısayolları al
        /// </summary>
        public IEnumerable<ShortcutInfo> GetAllShortcuts()
        {
            foreach (var binding in _shortcuts.Values)
            {
                yield return new ShortcutInfo
                {
                    Id = binding.Id,
                    KeyDisplay = binding.Gesture.DisplayString,
                    Description = binding.Description,
                    IsEnabled = binding.IsEnabled
                };
            }
        }

        /// <summary>
        /// Kısayol tuş kombinasyonunu değiştir
        /// </summary>
        public bool ChangeShortcutKey(string id, Key newKey, ModifierKeys newModifiers)
        {
            if (!_shortcuts.TryGetValue(id, out var binding))
                return false;

            var newGesture = new KeyGesture(newKey, newModifiers);

            // Çakışma kontrolü
            if (_gestureMap.ContainsKey(newGesture))
            {
                Log.Warning("[Shortcuts] Çakışma: {Gesture} zaten kullanılıyor", newGesture.DisplayString);
                return false;
            }

            // Eski gesture'ı kaldır
            _gestureMap.Remove(binding.Gesture);

            // Yeni gesture'ı ayarla
            binding.Gesture = newGesture;
            _gestureMap[newGesture] = id;

            Log.Information("[Shortcuts] {Id} güncellendi: {Gesture}", id, newGesture.DisplayString);
            return true;
        }

        #endregion

        #region Private Methods

        private void RegisterDefaultShortcuts()
        {
            // Stream kontrolü
            RegisterShortcut("stream.start", Key.F5, ModifierKeys.None,
                () => ShortcutActions.StartStream(),
                "Yayını Başlat");

            RegisterShortcut("stream.stop", Key.F6, ModifierKeys.None,
                () => ShortcutActions.StopStream(),
                "Yayını Durdur");

            RegisterShortcut("stream.toggle", Key.Space, ModifierKeys.Control,
                () => ShortcutActions.ToggleStream(),
                "Yayını Başlat/Durdur");

            // Kayıt
            RegisterShortcut("record.toggle", Key.R, ModifierKeys.Control,
                () => ShortcutActions.ToggleRecording(),
                "Kaydı Başlat/Durdur");

            // Ses
            RegisterShortcut("audio.mute", Key.M, ModifierKeys.Control,
                () => ShortcutActions.ToggleMute(),
                "Sesi Kapat/Aç");

            RegisterShortcut("audio.volume_up", Key.Up, ModifierKeys.Control,
                () => ShortcutActions.VolumeUp(),
                "Sesi Artır");

            RegisterShortcut("audio.volume_down", Key.Down, ModifierKeys.Control,
                () => ShortcutActions.VolumeDown(),
                "Sesi Azalt");

            // Navigasyon
            RegisterShortcut("nav.control", Key.D1, ModifierKeys.Control,
                () => ShortcutActions.NavigateTo(0),
                "Yayın Paneli");

            RegisterShortcut("nav.preview", Key.D2, ModifierKeys.Control,
                () => ShortcutActions.NavigateTo(1),
                "Önizleme");

            RegisterShortcut("nav.platforms", Key.D3, ModifierKeys.Control,
                () => ShortcutActions.NavigateTo(2),
                "Platformlar");

            RegisterShortcut("nav.chat", Key.D4, ModifierKeys.Control,
                () => ShortcutActions.NavigateTo(3),
                "Sohbet");

            RegisterShortcut("nav.settings", Key.D5, ModifierKeys.Control,
                () => ShortcutActions.NavigateTo(4),
                "Ayarlar");

            // Overlay
            RegisterShortcut("overlay.toggle", Key.O, ModifierKeys.Control,
                () => ShortcutActions.ToggleOverlay(),
                "Overlay Göster/Gizle");

            // Genel
            RegisterShortcut("app.fullscreen", Key.F11, ModifierKeys.None,
                () => ShortcutActions.ToggleFullscreen(),
                "Tam Ekran");

            RegisterShortcut("app.minimize", Key.M, ModifierKeys.Control | ModifierKeys.Shift,
                () => ShortcutActions.MinimizeToTray(),
                "Sistem Tepsisine Küçült");

            RegisterShortcut("app.help", Key.F1, ModifierKeys.None,
                () => ShortcutActions.ShowHelp(),
                "Yardım");
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isEnabled) return;

            // TextBox veya editable control'da iken kısayolları devre dışı bırak
            if (e.OriginalSource is System.Windows.Controls.TextBox ||
                e.OriginalSource is System.Windows.Controls.RichTextBox ||
                e.OriginalSource is System.Windows.Controls.PasswordBox)
            {
                return;
            }

            // DÜZELTME v57: Modifier tuşları tek başına basıldığında atla
            // KeyGesture sadece modifier + başka bir tuş kombinasyonunu destekler
            // Ctrl, Alt, Shift, Win tuşları tek başına KeyGesture oluşturamaz
            if (IsModifierKey(e.Key))
            {
                return;
            }

            try
            {
                var gesture = new KeyGesture(e.Key, Keyboard.Modifiers);

                if (_gestureMap.TryGetValue(gesture, out var id))
                {
                    if (_shortcuts.TryGetValue(id, out var binding) && binding.IsEnabled)
                    {
                        ExecuteShortcut(id);
                        e.Handled = true;
                    }
                }
            }
            catch (NotSupportedException)
            {
                // Geçersiz tuş kombinasyonu - sessizce atla
                // Örn: NumLock, CapsLock, bazı özel tuşlar
            }
        }

        /// <summary>
        /// DÜZELTME v57: Modifier tuşu kontrolü
        /// </summary>
        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin ||
                   key == Key.System;
        }

        private void ExecuteShortcut(string id)
        {
            if (!_shortcuts.TryGetValue(id, out var binding))
                return;

            if (!binding.IsEnabled)
                return;

            try
            {
                Log.Debug("[Shortcuts] Çalıştırılıyor: {Id}", id);
                binding.Action?.Invoke();

                OnShortcutTriggered?.Invoke(this, new ShortcutTriggeredEventArgs
                {
                    ShortcutId = id,
                    Description = binding.Description
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Shortcuts] Kısayol hatası: {Id}", id);
            }
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            if (_mainWindow != null)
            {
                _mainWindow.PreviewKeyDown -= OnPreviewKeyDown;
                _mainWindow.InputBindings.Clear();
                _mainWindow = null;
            }

            _shortcuts.Clear();
            _gestureMap.Clear();
        }

        #endregion
    }

    #region Types

    public class ShortcutBinding
    {
        public string Id { get; init; } = "";
        public KeyGesture Gesture { get; set; } = null!;
        public Action? Action { get; init; }
        public string Description { get; init; } = "";
        public bool IsEnabled { get; set; }
    }

    public class ShortcutInfo
    {
        public string Id { get; init; } = "";
        public string KeyDisplay { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsEnabled { get; init; }
    }

    public class ShortcutTriggeredEventArgs : EventArgs
    {
        public string ShortcutId { get; init; } = "";
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Kısayol aksiyonları
    /// </summary>
    public static class ShortcutActions
    {
        public static Action? OnStartStream { get; set; }
        public static Action? OnStopStream { get; set; }
        public static Action? OnToggleStream { get; set; }
        public static Action? OnToggleRecording { get; set; }
        public static Action? OnToggleMute { get; set; }
        public static Action? OnVolumeUp { get; set; }
        public static Action? OnVolumeDown { get; set; }
        public static Action<int>? OnNavigateTo { get; set; }
        public static Action? OnToggleOverlay { get; set; }
        public static Action? OnToggleFullscreen { get; set; }
        public static Action? OnMinimizeToTray { get; set; }
        public static Action? OnShowHelp { get; set; }

        public static void StartStream() => OnStartStream?.Invoke();
        public static void StopStream() => OnStopStream?.Invoke();
        public static void ToggleStream() => OnToggleStream?.Invoke();
        public static void ToggleRecording() => OnToggleRecording?.Invoke();
        public static void ToggleMute() => OnToggleMute?.Invoke();
        public static void VolumeUp() => OnVolumeUp?.Invoke();
        public static void VolumeDown() => OnVolumeDown?.Invoke();
        public static void NavigateTo(int tabIndex) => OnNavigateTo?.Invoke(tabIndex);
        public static void ToggleOverlay() => OnToggleOverlay?.Invoke();
        public static void ToggleFullscreen() => OnToggleFullscreen?.Invoke();
        public static void MinimizeToTray() => OnMinimizeToTray?.Invoke();
        public static void ShowHelp() => OnShowHelp?.Invoke();
    }

    /// <summary>
    /// ICommand implementasyonu
    /// </summary>
    public class RelayShortcutCommand : ICommand
    {
        private readonly Action _execute;

        public RelayShortcutCommand(Action execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }

    #endregion
}