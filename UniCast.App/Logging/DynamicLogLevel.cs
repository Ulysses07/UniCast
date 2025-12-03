using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace UniCast.App.Logging
{
    /// <summary>
    /// DÜZELTME v20: Dinamik Log Level yönetimi
    /// Runtime'da log seviyesini değiştirme imkanı
    /// </summary>
    public sealed class DynamicLogLevel
    {
        #region Singleton

        private static readonly Lazy<DynamicLogLevel> _instance =
            new(() => new DynamicLogLevel());

        public static DynamicLogLevel Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly LoggingLevelSwitch _levelSwitch;
        private readonly string _configFilePath;

        #endregion

        #region Events

        public event EventHandler<LogLevelChangedEventArgs>? OnLogLevelChanged;

        #endregion

        #region Properties

        public LogEventLevel CurrentLevel => _levelSwitch.MinimumLevel;
        public LoggingLevelSwitch LevelSwitch => _levelSwitch;

        #endregion

        #region Constructor

        private DynamicLogLevel()
        {
            _levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

            _configFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniCast", "loglevel.txt");

            // Kaydedilmiş seviyeyi yükle
            LoadSavedLevel();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Log seviyesini değiştir
        /// </summary>
        public void SetLevel(LogEventLevel level)
        {
            var oldLevel = _levelSwitch.MinimumLevel;

            if (oldLevel == level) return;

            _levelSwitch.MinimumLevel = level;

            Log.Information("[LogLevel] Seviye değiştirildi: {OldLevel} -> {NewLevel}", oldLevel, level);

            // Kaydet
            SaveLevel(level);

            // Event tetikle
            OnLogLevelChanged?.Invoke(this, new LogLevelChangedEventArgs
            {
                OldLevel = oldLevel,
                NewLevel = level
            });
        }

        /// <summary>
        /// Log seviyesini string'den ayarla
        /// </summary>
        public bool SetLevel(string levelName)
        {
            if (Enum.TryParse<LogEventLevel>(levelName, true, out var level))
            {
                SetLevel(level);
                return true;
            }

            Log.Warning("[LogLevel] Geçersiz seviye: {Level}", levelName);
            return false;
        }

        /// <summary>
        /// Debug moduna geç
        /// </summary>
        public void EnableDebugMode()
        {
            SetLevel(LogEventLevel.Debug);
        }

        /// <summary>
        /// Verbose moduna geç (en detaylı)
        /// </summary>
        public void EnableVerboseMode()
        {
            SetLevel(LogEventLevel.Verbose);
        }

        /// <summary>
        /// Normal moda dön
        /// </summary>
        public void ResetToDefault()
        {
            SetLevel(LogEventLevel.Information);
        }

        /// <summary>
        /// Sadece hataları logla
        /// </summary>
        public void ErrorsOnly()
        {
            SetLevel(LogEventLevel.Error);
        }

        /// <summary>
        /// Geçici olarak log seviyesini değiştir
        /// </summary>
        public IDisposable TemporaryLevel(LogEventLevel level)
        {
            var originalLevel = _levelSwitch.MinimumLevel;
            _levelSwitch.MinimumLevel = level;

            return new TemporaryLevelScope(this, originalLevel);
        }

        /// <summary>
        /// Mevcut seviyeleri al
        /// </summary>
        public static LogEventLevel[] GetAllLevels()
        {
            return (LogEventLevel[])Enum.GetValues(typeof(LogEventLevel));
        }

        /// <summary>
        /// Seviye adını al
        /// </summary>
        public static string GetLevelDisplayName(LogEventLevel level)
        {
            return level switch
            {
                LogEventLevel.Verbose => "Verbose (Çok Detaylı)",
                LogEventLevel.Debug => "Debug (Geliştirici)",
                LogEventLevel.Information => "Information (Normal)",
                LogEventLevel.Warning => "Warning (Uyarı)",
                LogEventLevel.Error => "Error (Hata)",
                LogEventLevel.Fatal => "Fatal (Kritik)",
                _ => level.ToString()
            };
        }

        #endregion

        #region Private Methods

        private void LoadSavedLevel()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var levelStr = File.ReadAllText(_configFilePath).Trim();
                    if (Enum.TryParse<LogEventLevel>(levelStr, out var level))
                    {
                        _levelSwitch.MinimumLevel = level;
                        Log.Debug("[LogLevel] Kaydedilmiş seviye yüklendi: {Level}", level);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[LogLevel] Seviye yükleme hatası");
            }
        }

        private void SaveLevel(LogEventLevel level)
        {
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_configFilePath, level.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[LogLevel] Seviye kaydetme hatası");
            }
        }

        #endregion

        #region TemporaryLevelScope

        private sealed class TemporaryLevelScope : IDisposable
        {
            private readonly DynamicLogLevel _manager;
            private readonly LogEventLevel _originalLevel;
            private bool _disposed;

            public TemporaryLevelScope(DynamicLogLevel manager, LogEventLevel originalLevel)
            {
                _manager = manager;
                _originalLevel = originalLevel;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _manager._levelSwitch.MinimumLevel = _originalLevel;
            }
        }

        #endregion
    }

    #region Types

    public class LogLevelChangedEventArgs : EventArgs
    {
        public LogEventLevel OldLevel { get; init; }
        public LogEventLevel NewLevel { get; init; }
    }

    #endregion

    #region Serilog Configuration Extension

    public static class SerilogConfigurationExtensions
    {
        /// <summary>
        /// Serilog'u dinamik log level ile yapılandır
        /// </summary>
        public static LoggerConfiguration WithDynamicLevel(
            this LoggerConfiguration configuration)
        {
            return configuration.MinimumLevel.ControlledBy(DynamicLogLevel.Instance.LevelSwitch);
        }
    }

    #endregion
}
