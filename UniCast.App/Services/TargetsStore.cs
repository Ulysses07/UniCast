using System.Collections.Generic;
using UniCast.Core.Models;

namespace UniCast.App.Services
{
    /// <summary>
    /// Yayın hedeflerini (RTMP URL'ler) saklar.
    /// DÜZELTME: BaseStore'dan türetilerek kod tekrarı azaltıldı.
    /// </summary>
    public sealed class TargetsStoreV2 : BaseStore<List<TargetItem>>
    {
        // Singleton instance
        private static readonly TargetsStoreV2 _instance = new();

        private TargetsStoreV2() : base("targets.json") { }

        /// <summary>
        /// Hedefleri yükler.
        /// </summary>
        public static List<TargetItem> LoadTargets() => _instance.Load();

        /// <summary>
        /// Hedefleri kaydeder.
        /// </summary>
        public static void SaveTargets(IReadOnlyCollection<TargetItem> items)
        {
            _instance.Save(new List<TargetItem>(items));
        }

        /// <summary>
        /// Varsayılan boş liste oluşturur.
        /// </summary>
        protected override List<TargetItem> CreateDefault() => new();
    }

    /// <summary>
    /// Geriye uyumluluk için eski API (static metotlar).
    /// Yeni kod TargetsStoreV2 kullanmalı.
    /// </summary>
    public static class TargetsStore
    {
        public static List<TargetItem> Load() => TargetsStoreV2.LoadTargets();

        public static void Save(IReadOnlyCollection<TargetItem> items) => TargetsStoreV2.SaveTargets(items);
    }
}