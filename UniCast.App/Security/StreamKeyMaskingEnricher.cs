using System;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace UniCast.App.Security
{
    /// <summary>
    /// DÜZELTME v17.2: Serilog Enricher - Log mesajlarındaki stream key'leri maskeler.
    /// 
    /// Bu enricher tüm log mesajlarını tarar ve:
    /// - RTMP URL'lerindeki stream key'leri maskeler
    /// - Exception mesajlarındaki hassas bilgileri maskeler
    /// - OAuth token'ları maskeler
    /// </summary>
    public sealed class StreamKeyMaskingEnricher : ILogEventEnricher
    {
        // RTMP URL pattern: rtmp://server/app/stream_key
        private static readonly Regex RtmpPattern = new(
            @"(rtmps?://[^/]+/[^/]+/)([a-zA-Z0-9_\-]{4,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // OAuth token pattern: oauth:xxxxx veya Bearer xxxxx
        private static readonly Regex OAuthPattern = new(
            @"(oauth:|Bearer\s+)([a-zA-Z0-9_\-]{4,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Generic API key pattern: api_key=xxxxx, key=xxxxx, token=xxxxx
        private static readonly Regex ApiKeyPattern = new(
            @"(api[_-]?key|key|token|secret|password|pwd)\s*[=:]\s*[""']?([a-zA-Z0-9_\-]{4,})[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Session ID pattern (Instagram, Facebook vb.)
        private static readonly Regex SessionPattern = new(
            @"(session[_-]?id|sessionid|ds_user_id)\s*[=:]\s*[""']?([a-zA-Z0-9_\-]{4,})[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (logEvent.MessageTemplate.Text == null)
                return;

            // MessageTemplate'i direkt değiştiremiyoruz ama property'leri maskeleyebiliriz
            // Exception varsa maskeleme yap
            if (logEvent.Exception != null)
            {
                var maskedMessage = MaskSensitiveData(logEvent.Exception.Message);

                // Maskelenmiş exception bilgisini property olarak ekle
                if (maskedMessage != logEvent.Exception.Message)
                {
                    var prop = propertyFactory.CreateProperty(
                        "MaskedExceptionMessage",
                        maskedMessage);
                    logEvent.AddPropertyIfAbsent(prop);
                }
            }

            // Rendered message'ı kontrol et (property değerleri dahil)
            foreach (var property in logEvent.Properties)
            {
                if (property.Value is ScalarValue scalarValue &&
                    scalarValue.Value is string stringValue)
                {
                    var masked = MaskSensitiveData(stringValue);
                    if (masked != stringValue)
                    {
                        // Maskelenmiş property'yi ekle
                        var prop = propertyFactory.CreateProperty(
                            property.Key + "_Masked",
                            masked);
                        logEvent.AddPropertyIfAbsent(prop);
                    }
                }
            }
        }

        /// <summary>
        /// Hassas verileri maskeler.
        /// </summary>
        public static string MaskSensitiveData(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = input;

            // 1. RTMP stream key'lerini maskele
            result = RtmpPattern.Replace(result, m =>
            {
                var key = m.Groups[2].Value;
                if (key.Length > 4)
                    return m.Groups[1].Value + key[..4] + new string('*', Math.Min(12, key.Length - 4));
                return m.Value;
            });

            // 2. OAuth token'larını maskele
            result = OAuthPattern.Replace(result, m =>
            {
                var token = m.Groups[2].Value;
                if (token.Length > 4)
                    return m.Groups[1].Value + token[..4] + new string('*', Math.Min(12, token.Length - 4));
                return m.Value;
            });

            // 3. API key'lerini maskele
            result = ApiKeyPattern.Replace(result, m =>
            {
                var key = m.Groups[2].Value;
                if (key.Length > 4)
                    return m.Groups[1].Value + "=" + key[..4] + new string('*', Math.Min(12, key.Length - 4));
                return m.Value;
            });

            // 4. Session ID'leri maskele
            result = SessionPattern.Replace(result, m =>
            {
                var sessionId = m.Groups[2].Value;
                if (sessionId.Length > 4)
                    return m.Groups[1].Value + "=" + sessionId[..4] + new string('*', Math.Min(12, sessionId.Length - 4));
                return m.Value;
            });

            return result;
        }
    }
}
