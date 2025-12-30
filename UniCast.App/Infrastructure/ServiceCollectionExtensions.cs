using Microsoft.Extensions.DependencyInjection;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Pipeline;
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

            // Device Service - Kamera/mikrofon listesi
            services.AddSingleton<IDeviceService, DeviceService>();

            // Chat Bus - Tüm chat kaynaklarını birleştirir
            services.AddSingleton(ChatBus.Instance);

            // Audio Service - Ses seviyesi izleme
            services.AddSingleton<IAudioService, AudioService>();

            // Preview Service - FFmpeg-First Pipeline (preview + yayın)
            services.AddSingleton<FFmpegPreviewService>();
            services.AddSingleton<IPreviewService>(sp => sp.GetRequiredService<FFmpegPreviewService>());

            // --- ViewModels ---

            // SettingsViewModel - Ayarlar sayfası
            services.AddTransient<SettingsViewModel>();

            // TargetsViewModel - Platform hedefleri
            services.AddTransient<TargetsViewModel>();

            // ChatViewModel - Sohbet akışı
            services.AddTransient<ChatViewModel>();

            // PreviewViewModel - DI'dan IPreviewService alıyor
            services.AddTransient<PreviewViewModel>(sp =>
            {
                var previewService = sp.GetRequiredService<IPreviewService>();
                return new PreviewViewModel(previewService);
            });

            // ControlViewModel - Ana kontrol paneli (artık IStreamController kullanmıyor)
            services.AddTransient<ControlViewModel>(sp =>
            {
                var targetsVm = sp.GetRequiredService<TargetsViewModel>();
                return new ControlViewModel(
                    () => (targetsVm.Targets, SettingsStore.Load())
                );
            });

            return services;
        }
    }
}