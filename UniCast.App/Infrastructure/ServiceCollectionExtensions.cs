using Microsoft.Extensions.DependencyInjection;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;
using UniCast.App.ViewModels;
using UniCast.Core.Chat;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// Dependency Injection servis kayıtları.
    /// Tüm servisler burada merkezi olarak yönetilir.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddUniCastServices(this IServiceCollection services)
        {
            // --- Singleton Servisler (Uygulama boyunca tek instance) ---

            // Stream Controller - Ana yayın yöneticisi
            services.AddSingleton<IStreamController, StreamController>();

            // Device Service - Kamera/mikrofon listesi
            services.AddSingleton<IDeviceService, DeviceService>();

            // Chat Bus - Tüm chat kaynaklarını birleştirir
            // DÜZELTME: ChatConstants kullanımı
            services.AddSingleton<ChatBus>(sp => new ChatBus(
                maxPerSecond: ChatConstants.MaxMessagesPerSecond,
                cacheCapacity: ChatConstants.CacheCapacity
            ));

            // Audio Service - Ses seviyesi izleme
            services.AddSingleton<AudioService>();

            // Preview Service - Kamera önizleme (Singleton - tek kamera kaynağı)
            services.AddSingleton<PreviewService>();

            // --- Transient Servisler (Her istekte yeni instance) ---

            // Chat Ingestor'lar
            services.AddTransient<YouTubeChatIngestor>();
            services.AddTransient<TikTokChatIngestor>();
            services.AddTransient<InstagramChatIngestor>();
            services.AddTransient<FacebookChatIngestor>();

            // --- ViewModels ---

            // SettingsViewModel - Ayarlar sayfası
            services.AddTransient<SettingsViewModel>();

            // TargetsViewModel - Platform hedefleri
            services.AddTransient<TargetsViewModel>();

            // ChatViewModel - Sohbet akışı
            services.AddTransient<ChatViewModel>();

            // PreviewViewModel - DI'dan PreviewService alıyor
            services.AddTransient<PreviewViewModel>(sp =>
            {
                var previewService = sp.GetRequiredService<PreviewService>();
                return new PreviewViewModel(previewService);
            });

            // ControlViewModel - Ana kontrol paneli
            services.AddTransient<ControlViewModel>(sp =>
            {
                var stream = sp.GetRequiredService<IStreamController>();
                var targetsVm = sp.GetRequiredService<TargetsViewModel>();
                return new ControlViewModel(
                    stream,
                    () => (targetsVm.Targets, SettingsStore.Load())
                );
            });

            return services;
        }
    }
}