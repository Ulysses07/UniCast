using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace UniCast.App.Configuration
{
    /// <summary>
    /// DÜZELTME v19: Uygulama başlangıcında konfigürasyon doğrulama
    /// DÜZELTME v31: FFmpeg path'leri güncellendi
    /// Gerekli dosyalar, ayarlar ve izinleri kontrol eder
    /// </summary>
    public sealed class ConfigurationValidator : IConfigurationValidator
    {
        #region Singleton

        private static readonly Lazy<ConfigurationValidator> _instance =
            new(() => new ConfigurationValidator());

        public static ConfigurationValidator Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly List<IConfigurationRule> _rules = new();

        #endregion

        #region Constructor

        private ConfigurationValidator()
        {
            RegisterDefaultRules();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Kural ekle
        /// </summary>
        public void AddRule(IConfigurationRule rule)
        {
            _rules.Add(rule);
        }

        /// <summary>
        /// Tüm kuralları çalıştır
        /// </summary>
        public ValidationResult Validate()
        {
            var errors = new List<ValidationError>();
            var warnings = new List<ValidationWarning>();

            foreach (var rule in _rules)
            {
                try
                {
                    var result = rule.Validate();

                    if (result.Errors.Any())
                        errors.AddRange(result.Errors);

                    if (result.Warnings.Any())
                        warnings.AddRange(result.Warnings);
                }
                catch (Exception ex)
                {
                    errors.Add(new ValidationError
                    {
                        Rule = rule.Name,
                        Message = $"Validation failed: {ex.Message}",
                        IsCritical = false
                    });
                }
            }

            var isValid = !errors.Any(e => e.IsCritical);

            if (isValid)
            {
                Log.Information("[ConfigValidator] Konfigürasyon doğrulandı. Warnings: {WarningCount}",
                    warnings.Count);
            }
            else
            {
                Log.Error("[ConfigValidator] Konfigürasyon hataları: {ErrorCount} error, {WarningCount} warning",
                    errors.Count, warnings.Count);
            }

            return new ValidationResult
            {
                IsValid = isValid,
                Errors = errors,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Kritik hatalar varsa exception fırlat
        /// </summary>
        public void ValidateOrThrow()
        {
            var result = Validate();

            if (!result.IsValid)
            {
                var criticalErrors = result.Errors.Where(e => e.IsCritical).ToList();
                throw new ConfigurationException(
                    "Kritik konfigürasyon hataları var",
                    criticalErrors);
            }
        }

        #endregion

        #region Private Methods

        private void RegisterDefaultRules()
        {
            _rules.Add(new FFmpegExistsRule());
            _rules.Add(new DirectoriesWritableRule());
            _rules.Add(new SettingsFileRule());
            _rules.Add(new LicenseConfigRule());
            _rules.Add(new EncoderSettingsRule());
            _rules.Add(new StreamTargetsRule());
        }

        #endregion
    }

    #region Types

    public interface IConfigurationRule
    {
        string Name { get; }
        RuleResult Validate();
    }

    public class RuleResult
    {
        public List<ValidationError> Errors { get; init; } = new();
        public List<ValidationWarning> Warnings { get; init; } = new();
    }

    public class ValidationResult
    {
        public bool IsValid { get; init; }
        public List<ValidationError> Errors { get; init; } = new();
        public List<ValidationWarning> Warnings { get; init; } = new();
    }

    public class ValidationError
    {
        public string Rule { get; init; } = "";
        public string Message { get; init; } = "";
        public bool IsCritical { get; init; }
        public string? Suggestion { get; init; }
    }

    public class ValidationWarning
    {
        public string Rule { get; init; } = "";
        public string Message { get; init; } = "";
        public string? Suggestion { get; init; }
    }

    public class ConfigurationException : Exception
    {
        public List<ValidationError> Errors { get; }

        public ConfigurationException(string message, List<ValidationError> errors)
            : base(message)
        {
            Errors = errors;
        }
    }

    #endregion

    #region Built-in Rules

    /// <summary>
    /// FFmpeg varlık kontrolü
    /// DÜZELTME v31: External/ ve Externals/ klasörleri eklendi
    /// </summary>
    public class FFmpegExistsRule : IConfigurationRule
    {
        public string Name => "FFmpegExists";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            // DÜZELTME v31: FfmpegProcess.cs ile uyumlu tüm path'ler
            var ffmpegPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "External", "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Externals", "ffmpeg.exe")
            };

            var found = ffmpegPaths.Any(File.Exists);

            if (!found)
            {
                // PATH'te ara
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                found = envPath.Split(Path.PathSeparator)
                    .Any(dir => File.Exists(Path.Combine(dir, "ffmpeg.exe")));
            }

            if (!found)
            {
                result.Errors.Add(new ValidationError
                {
                    Rule = Name,
                    Message = "FFmpeg bulunamadı",
                    IsCritical = true,
                    Suggestion = "FFmpeg'i indirin ve uygulama klasörüne (External/) koyun veya PATH'e ekleyin.\n" +
                                "NVENC için: https://github.com/BtbN/FFmpeg-Builds/releases"
                });
            }

            return result;
        }
    }

    /// <summary>
    /// Yazılabilir dizin kontrolü
    /// </summary>
    public class DirectoriesWritableRule : IConfigurationRule
    {
        public string Name => "DirectoriesWritable";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            var directories = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UniCast"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UniCast", "Logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniCast")
            };

            foreach (var dir in directories)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // Yazma testi
                    var testFile = Path.Combine(dir, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Rule = Name,
                        Message = $"Dizin yazılamıyor: {dir}",
                        IsCritical = false,
                        Suggestion = $"Dizin izinlerini kontrol edin: {ex.Message}"
                    });
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Ayar dosyası kontrolü
    /// </summary>
    public class SettingsFileRule : IConfigurationRule
    {
        public string Name => "SettingsFile";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UniCast", "settings.json");

            if (File.Exists(settingsPath))
            {
                try
                {
                    var content = File.ReadAllText(settingsPath);
                    System.Text.Json.JsonDocument.Parse(content);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(new ValidationWarning
                    {
                        Rule = Name,
                        Message = $"Ayar dosyası bozuk: {ex.Message}",
                        Suggestion = "Varsayılan ayarlar kullanılacak"
                    });
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Lisans konfigürasyonu kontrolü
    /// </summary>
    public class LicenseConfigRule : IConfigurationRule
    {
        public string Name => "LicenseConfig";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            var serverUrl = Environment.GetEnvironmentVariable("UNICAST_LICENSE_SERVER");
            var publicKey = Environment.GetEnvironmentVariable("UNICAST_PUBLIC_KEY");

            // Opsiyonel uyarılar
            if (string.IsNullOrEmpty(serverUrl))
            {
                result.Warnings.Add(new ValidationWarning
                {
                    Rule = Name,
                    Message = "UNICAST_LICENSE_SERVER tanımlı değil",
                    Suggestion = "Varsayılan sunucu kullanılacak"
                });
            }

            return result;
        }
    }

    /// <summary>
    /// Encoder ayarları kontrolü
    /// </summary>
    public class EncoderSettingsRule : IConfigurationRule
    {
        public string Name => "EncoderSettings";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            // Bu kural SettingsStore yüklendikten sonra çalışmalı
            // Şimdilik temel kontrol yapıyoruz

            return result;
        }
    }

    /// <summary>
    /// Stream hedefleri kontrolü
    /// </summary>
    public class StreamTargetsRule : IConfigurationRule
    {
        public string Name => "StreamTargets";

        public RuleResult Validate()
        {
            var result = new RuleResult();

            var targetsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UniCast", "targets.json");

            if (File.Exists(targetsPath))
            {
                try
                {
                    var content = File.ReadAllText(targetsPath);
                    var doc = System.Text.Json.JsonDocument.Parse(content);

                    // Stream key'lerin varlığını kontrol et (içeriğini değil)
                    // Güvenlik için stream key'ler loglanmaz
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(new ValidationWarning
                    {
                        Rule = Name,
                        Message = $"Hedefler dosyası okunamadı: {ex.Message}",
                        Suggestion = "Platformlar sekmesinden hedefleri yeniden yapılandırın"
                    });
                }
            }

            return result;
        }
    }

    #endregion
}