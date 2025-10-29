using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using UniCast.Core;

namespace UniCast.App.ViewModels
{
    // Tek bir RTMP hedef öğesi (YouTube, TikTok vs.)
    public sealed class TargetItem : INotifyPropertyChanged
    {
        private string _url = "";
        private bool _enabled;

        public string Name { get; }
        public Platform Platform { get; }

        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public TargetItem(string name, Platform platform, string url = "", bool enabled = false)
        {
            Name = name;
            Platform = platform;
            _url = url;
            _enabled = enabled;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ViewModel: RTMP hedeflerinin tamamını tutar
    public sealed class TargetsViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TargetItem> Targets { get; } = new()
        {
            new TargetItem("YouTube",   Platform.YouTube),
            new TargetItem("Facebook",  Platform.Facebook),
            new TargetItem("TikTok",    Platform.TikTok),
            new TargetItem("Instagram", Platform.Instagram),
        };

        private string _warnings = "";
        public string Warnings
        {
            get => _warnings;
            set
            {
                if (_warnings != value)
                {
                    _warnings = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Etkin (işaretli) hedeflerin dolu URL'lerini döndürür.
        /// </summary>
        public IEnumerable<string> GetEnabledUrls()
        {
            // Hiç hedef yoksa veya kullanıcı işaretlememişse boş döner
            var urls = Targets
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url))
                .Select(t => t.Url.Trim())
                .ToList();

            // Hata durumunda debug log ekle (geliştiriciye kolaylık)
#if DEBUG
            if (urls.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[TargetsViewModel] GetEnabledUrls() boş döndü!");
            }
#endif
            return urls;
        }

        /// <summary>
        /// Aktif preset'e göre platform kısıtlamalarını kontrol eder.
        /// </summary>
        public void UpdateWarnings(EncoderProfile preset)
        {
            var msgs = new List<string>();

            foreach (var t in Targets)
            {
                if (!t.Enabled || string.IsNullOrWhiteSpace(t.Url))
                    continue;

                if (!PlatformRules.IsPresetAllowed(t.Platform, preset, out var reason))
                    msgs.Add($"{t.Name}: {reason}");
            }

            Warnings = msgs.Count > 0
                ? string.Join("\n", msgs)
                : "";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
