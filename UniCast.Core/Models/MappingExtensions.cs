using System.Collections.Generic;

namespace UniCast.Core.Models
{
    public static class MappingExtensions
    {
        public static IEnumerable<StreamTarget> ToStreamTargets(this IEnumerable<TargetItem> items, Settings.SettingsData settings)
        {
            if (items == null) yield break;
            foreach (var i in items)
            {
                yield return new StreamTarget
                {
                    Platform = i.Platform,
                    DisplayName = i.Name,
                    StreamKey = i.Key,
                    Url = i.Url,
                    Enabled = i.Enabled
                };
            }
        }
    }
}
