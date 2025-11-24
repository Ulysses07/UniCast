using System;
using UniCast.Core.Streaming; // StreamTarget ve StreamPlatform burada varsayıldı

namespace UniCast.Encoder.Extensions
{
    public static class StreamTargetExtensions
    {
        /// <summary>
        /// Hedef platforma göre tam RTMP/RTMPS publish URL'sini üretir.
        /// - Url boşsa platforma özgü kök + StreamKey kullanılır.
        /// - Url doluysa ve StreamKey içermiyorsa sonuna eklenir.
        /// </summary>
        public static string ResolveUrl(this StreamTarget t)
        {
            ArgumentNullException.ThrowIfNull(t);

            string? url = t.Url;
            string key = t.StreamKey ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                return t.Platform switch
                {
                    StreamPlatform.YouTube => $"rtmp://a.rtmp.youtube.com/live2/{key}",
                    StreamPlatform.Facebook => $"rtmps://live-api-s.facebook.com:443/rtmp/{key}",
                    StreamPlatform.Twitch => $"rtmp://live.twitch.tv/app/{key}",
                    _ when !string.IsNullOrWhiteSpace(key)
                                            => $"rtmp://localhost/live/{key}", // varsayılan (geliştirme)
                    _ => throw new InvalidOperationException("Target Url/StreamKey belirtilmemiş.")
                };
            }

            if (!string.IsNullOrWhiteSpace(key) && !url.Contains(key, StringComparison.Ordinal))
            {
                var sep = url.EndsWith("/") ? "" : "/";
                url = $"{url}{sep}{key}";
            }
            return url;
        }
    }
}
