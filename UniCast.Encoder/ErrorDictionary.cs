using System.Collections.Generic;

namespace UniCast.Encoder
{
    public static class ErrorDictionary
    {
        // Pattern -> Meaning mapping
        public static readonly Dictionary<string, string> Map = new()
        {
            { "Connection timed out", "Sunucuya bağlanılamadı. İnternet bağlantınızı ve hedef RTMP URL'yi kontrol edin." },
            { "Connection refused", "Yayın sunucusu bağlantıyı reddetti. Stream key yanlış olabilir veya sunucu kapalı." },
            { "Server error", "Platform yayın isteğini kabul etmedi. Stream key’i ve RTMP adresini doğrulayın." },
            { "Unknown error occurred", "FFmpeg internal error. Uygulamayı yeniden başlatmayı deneyin." },
            { "Broken pipe", "RTMP bağlantısı koptu. Ağ bağlantısını kontrol edin." },
            { "Invalid data found", "RTMP format hatası. Stream key veya URL hatalı olabilir." },
            { "No route to host", "Sunucuya ulaşım yok. VPN/Proxy kapatın — DNS kontrol edin." },
            { "TLS handshake", "RTMPS handshake başarısız. Firewall/SSL proxy engelliyor olabilir." },
            { "403 Forbidden", "Platform stream key'i reddetti. Büyük ihtimalle yanlış stream key." },
            { "401 Unauthorized", "Yetkisiz yayın. Yayın anahtarını kontrol edin." },
            { "Could not write header", "RTMP bağlantısı başladı ama sunucu veri kabul etmiyor. Yanlış yayın formatı olabilir." },
        };

        public static string Translate(string ffmpegLog)
        {
            string lower = ffmpegLog.ToLowerInvariant();
            foreach (var kv in Map)
            {
                if (lower.Contains(kv.Key.ToLowerInvariant()))
                    return kv.Value;
            }

            // Default fallback
            return "Yayın hatası oluştu. RTMP adresini, stream key’i ve internet bağlantısını kontrol edin.";
        }
    }
}
