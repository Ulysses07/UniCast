using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using UniCast.Core.Streaming;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// Platform enum değerini emoji/ikona çevirir
    /// </summary>
    public class PlatformToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is StreamPlatform platform)
            {
                return platform switch
                {
                    StreamPlatform.YouTube => "▶",
                    StreamPlatform.Twitch => "📺",
                    StreamPlatform.TikTok => "♪",
                    StreamPlatform.Instagram => "📷",
                    StreamPlatform.Facebook => "f",
                    StreamPlatform.Custom => "🔗",
                    _ => "●"
                };
            }
            return "●";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Platform enum değerini arka plan rengine çevirir
    /// </summary>
    public class PlatformToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is StreamPlatform platform)
            {
                var colorHex = platform switch
                {
                    StreamPlatform.YouTube => "#FF0000",      // Kırmızı
                    StreamPlatform.Twitch => "#9146FF",       // Mor
                    StreamPlatform.TikTok => "#000000",       // Siyah
                    StreamPlatform.Instagram => "#E4405F",    // Pembe/Kırmızı
                    StreamPlatform.Facebook => "#1877F2",     // Mavi
                    StreamPlatform.Custom => "#666666",       // Gri
                    _ => "#666666"
                };

                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Platform enum değerini kısa metin etiketine çevirir
    /// </summary>
    public class PlatformToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is StreamPlatform platform)
            {
                return platform switch
                {
                    StreamPlatform.YouTube => "YT",
                    StreamPlatform.Twitch => "TW",
                    StreamPlatform.TikTok => "TT",
                    StreamPlatform.Instagram => "IG",
                    StreamPlatform.Facebook => "FB",
                    StreamPlatform.Custom => "⚙",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}