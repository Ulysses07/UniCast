using Microsoft.Extensions.DependencyInjection;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
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
            services.AddSingleton<IStreamController, StreamControllerAdapter>();

            // Device Service - Kamera/mikrofon listesi
            services.AddSingleton<IDeviceService, DeviceService>();

            // Chat Bus - Tüm chat kaynaklarını birleştirir
            services.AddSingleton(ChatBus.Instance);

            // Audio Service - Ses seviyesi izleme
            services.AddSingleton<IAudioService, AudioService>();

            // Preview Service - Kamera önizleme (Singleton - tek kamera kaynağı)
            services.AddSingleton<IPreviewService, PreviewService>();

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
                var previewService = sp.GetRequiredService<IPreviewService>();
                return new PreviewViewModel((PreviewService)previewService);
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