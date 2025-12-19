using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using UniCast.Core.Chat;
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

    /// <summary>
    /// ChatPlatform enum değerini ikona çevirir (Chat mesajları için)
    /// </summary>
    public class ChatPlatformToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChatPlatform platform)
            {
                return platform switch
                {
                    ChatPlatform.YouTube => "▶",
                    ChatPlatform.Twitch => "📺",
                    ChatPlatform.TikTok => "♪",
                    ChatPlatform.Instagram => "📷",
                    ChatPlatform.Facebook => "f",
                    ChatPlatform.Twitter => "𝕏",
                    ChatPlatform.Discord => "💬",
                    ChatPlatform.Kick => "K",
                    _ => "●"
                };
            }
            return "●";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// ChatPlatform enum değerini renge çevirir (Chat mesajları için)
    /// </summary>
    public class ChatPlatformToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChatPlatform platform)
            {
                var colorHex = platform switch
                {
                    ChatPlatform.YouTube => "#FF0000",      // Kırmızı
                    ChatPlatform.Twitch => "#9146FF",       // Mor
                    ChatPlatform.TikTok => "#00F2EA",       // Turkuaz
                    ChatPlatform.Instagram => "#E4405F",    // Pembe
                    ChatPlatform.Facebook => "#1877F2",     // Mavi
                    ChatPlatform.Twitter => "#1DA1F2",      // Açık Mavi
                    ChatPlatform.Discord => "#5865F2",      // Discord Moru
                    ChatPlatform.Kick => "#53FC18",         // Yeşil
                    _ => "#888888"
                };

                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// ChatPlatform enum değerini kısa etikete çevirir
    /// </summary>
    public class ChatPlatformToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChatPlatform platform)
            {
                return platform switch
                {
                    ChatPlatform.YouTube => "YT",
                    ChatPlatform.Twitch => "TW",
                    ChatPlatform.TikTok => "TT",
                    ChatPlatform.Instagram => "IG",
                    ChatPlatform.Facebook => "FB",
                    ChatPlatform.Twitter => "X",
                    ChatPlatform.Discord => "DC",
                    ChatPlatform.Kick => "KK",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}