using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Serilog;

namespace UniCast.App.Services
{
    /// <summary>
    /// JSON dosya tabanlı storage için temel sınıf.
    /// Thread-safe okuma/yazma, atomic write ve retry mekanizması sağlar.
    /// </summary>
    /// <typeparam name="T">Saklanacak veri tipi</typeparam>
    public abstract class BaseStore<T> where T : class, new()
    {
        private static readonly string BaseDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");

        private readonly string _filePath;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 100;

        protected static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Store oluşturur.
        /// </summary>
        /// <param name="fileName">Dosya adı (örn: "settings.json")</param>
        protected BaseStore(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentNullException(nameof(fileName));

            _filePath = Path.Combine(BaseDir, fileName);
        }

        /// <summary>
        /// Dosya yolunu döndürür.
        /// </summary>
        protected string FilePath => _filePath;

        /// <summary>
        /// Verileri yükler (thread-safe).
        /// </summary>
        public T Load()
        {
            _lock.EnterReadLock();
            try
            {
                return LoadInternal();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Verileri kaydeder (thread-safe, retry mekanizmalı).
        /// </summary>
        public void Save(T data)
        {
            ArgumentNullException.ThrowIfNull(data);

            _lock.EnterWriteLock();
            try
            {
                SaveInternalWithRetry(data);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Atomic read-modify-write işlemi.
        /// </summary>
        public void Update(Action<T> modifier)
        {
            ArgumentNullException.ThrowIfNull(modifier);

            _lock.EnterWriteLock();
            try
            {
                var data = LoadInternal();
                modifier(data);
                SaveInternalWithRetry(data);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Alt sınıfların varsayılan veri oluşturması için.
        /// </summary>
        protected virtual T CreateDefault() => new T();

        /// <summary>
        /// Alt sınıfların veriyi normalize etmesi için (opsiyonel).
        /// </summary>
        protected virtual void Normalize(T data) { }

        /// <summary>
        /// Yükleme sonrası dönüşüm için (opsiyonel).
        /// </summary>
        protected virtual T PostLoad(T data) => data;

        /// <summary>
        /// Kayıt öncesi dönüşüm için (opsiyonel).
        /// </summary>
        protected virtual T PreSave(T data) => data;

        private T LoadInternal()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    var defaultData = CreateDefault();
                    Normalize(defaultData);
                    return defaultData;
                }

                string json;
                using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(fs))
                {
                    json = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    var defaultData = CreateDefault();
                    Normalize(defaultData);
                    return defaultData;
                }

                var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (data == null)
                {
                    var defaultData = CreateDefault();
                    Normalize(defaultData);
                    return defaultData;
                }

                Normalize(data);
                return PostLoad(data);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "[{StoreName}] JSON parse hatası, varsayılan değerler kullanılıyor", GetType().Name);
                BackupCorruptedFile();
                var defaultData = CreateDefault();
                Normalize(defaultData);
                return defaultData;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{StoreName}] Yükleme hatası", GetType().Name);
                var defaultData = CreateDefault();
                Normalize(defaultData);
                return defaultData;
            }
        }

        private void SaveInternalWithRetry(T data)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt < MAX_RETRY_ATTEMPTS; attempt++)
            {
                try
                {
                    SaveInternal(data);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    Log.Debug("[{StoreName}] Yazma hatası (deneme {Attempt}): {Message}",
                        GetType().Name, attempt + 1, ex.Message);

                    if (attempt < MAX_RETRY_ATTEMPTS - 1)
                    {
                        Thread.Sleep(RETRY_DELAY_MS * (attempt + 1));
                    }
                }
            }

            throw new IOException($"Veri kaydedilemedi ({MAX_RETRY_ATTEMPTS} deneme sonrası)", lastException);
        }

        private void SaveInternal(T data)
        {
            // Dizini oluştur
            if (!Directory.Exists(BaseDir))
                Directory.CreateDirectory(BaseDir);

            var dataToSave = PreSave(data);
            var json = JsonSerializer.Serialize(dataToSave, JsonOptions);

            // Atomic write
            var tempPath = _filePath + ".tmp";
            var backupPath = _filePath + ".bak";

            try
            {
                // 1. Geçici dosyaya yaz
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(true);
                }

                // 2. Mevcut dosyayı yedekle
                if (File.Exists(_filePath))
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(_filePath, backupPath);
                }

                // 3. Geçici dosyayı ana dosya yap
                File.Move(tempPath, _filePath);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    try { File.Delete(tempPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BaseStore] Temp dosya silme hatası: {ex.Message}"); }
                }
                throw;
            }
        }

        private void BackupCorruptedFile()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var corruptPath = _filePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Move(_filePath, corruptPath);
                    Log.Information("[{StoreName}] Bozuk dosya yedeklendi: {Path}", GetType().Name, corruptPath);
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[BaseStore.BackupCorruptedFile] Yedekleme hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Dosyanın var olup olmadığını kontrol eder.
        /// </summary>
        public bool Exists() => File.Exists(_filePath);

        /// <summary>
        /// Dosyayı siler.
        /// </summary>
        public void Delete()
        {
            _lock.EnterWriteLock();
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }
}