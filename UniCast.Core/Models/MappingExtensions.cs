using System.Collections.Generic;
using System.Linq;
using UniCast.Core.Streaming;

namespace UniCast.Core.Models
{
    public static class MappingExtensions
    {
        public static List<StreamTarget> ToStreamTargets(this IEnumerable<TargetItem> items)
        {
            if (items == null) return [];

            return items.Select(i => new StreamTarget
            {
                Platform = i.Platform, // Artık türler uyumlu (StreamPlatform)
                DisplayName = i.DisplayName,
                Url = i.Url,
                StreamKey = i.StreamKey, // İsimler uyumlu
                Enabled = i.Enabled
            }).ToList();
        }
    }
}