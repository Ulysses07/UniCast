using System;

namespace UniCast.Core.Models
{
    /// <summary>Encoder loglarından türetilen basit yayın metrikleri.</summary>
    public sealed class StreamMetric
    {
        public DateTime TimestampUtc { get; set; }
        public double? Fps { get; set; }
        public double? BitrateKbps { get; set; }
    }
}
