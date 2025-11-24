using System;
using System.Collections.Generic;
using UniCast.Core.Core;
using UniCast.Core.Streaming;

namespace UniCast.Core.Models
{
    public static class MappingExtensions
    {
        /// <summary>
        /// UI’daki TargetItem listesini encoder’ın kullandığı StreamTarget listesine dönüştürür.
        /// Sadece Enabled == true olanları alır. Platform enum dönüşümü güvenli yapılır.
        /// </summary>
        public static IReadOnlyList<StreamTarget> ToStreamTargets(this IEnumerable<TargetItem> items)
        {
            var list = new List<StreamTarget>();
            if (items == null) return list;

            foreach (var it in items)
            {
                if (it == null || !it.Enabled) continue;

                list.Add(new StreamTarget
                {
                    Platform = MapPlatform(it.Platform),
                    Url = it.Url,
                    StreamKey = it.Key,
                    Enabled = true,
                    DisplayName = string.IsNullOrWhiteSpace(it.DisplayName) ? it.Platform.ToString() : it.DisplayName
                });
            }
            return list;
        }

        /// <summary>
        /// UniCast.Core.Platform → UniCast.Core.Models.StreamPlatform map’i
        /// </summary>
        private static StreamPlatform MapPlatform(Platform p)
        {
            // İsim eşlemeyle dene, olmazsa Custom
            try
            {
                var name = Enum.GetName(typeof(Platform), p);
                if (name != null && Enum.TryParse<StreamPlatform>(name, ignoreCase: true, out var sp))
                    return sp;
            }
            catch { }
            return StreamPlatform.Custom;
        }
    }
}
