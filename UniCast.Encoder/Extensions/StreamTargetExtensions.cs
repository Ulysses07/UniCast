using UniCast.Core.Streaming;

namespace UniCast.Encoder.Extensions
{
    internal static class StreamTargetExtensions
    {
        /// <summary>
        /// RTMP çıkış URL'sini, varsa stream key ile birleştirir.
        /// "rtmp://server/app" + "streamkey" => "rtmp://server/app/streamkey"
        /// Stream key boşsa URL olduğu gibi döner.
        /// </summary>
        public static string ResolveUrl(this StreamTarget target)
        {
            var u = target?.Url?.Trim() ?? string.Empty;
            var k = target?.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(u)) return string.Empty;
            if (string.IsNullOrWhiteSpace(k)) return u;
            return u.EndsWith("/") ? (u + k) : (u + "/" + k);
        }
    }
}
