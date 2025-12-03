using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v20: Async File I/O Helper
    /// Disk işlemlerini async yaparak UI thread'i bloklamamayı sağlar
    /// </summary>
    public static class AsyncFileHelper
    {
        #region Configuration

        private const int DefaultBufferSize = 4096;
        private const FileOptions AsyncOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;

        #endregion

        #region Read Operations

        /// <summary>
        /// Dosyayı async olarak oku
        /// </summary>
        public static async Task<string> ReadAllTextAsync(
            string path,
            CancellationToken ct = default)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>
        /// Dosyayı async olarak byte array olarak oku
        /// </summary>
        public static async Task<byte[]> ReadAllBytesAsync(
            string path,
            CancellationToken ct = default)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            var length = stream.Length;
            var buffer = new byte[length];

            var totalRead = 0;
            while (totalRead < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
                if (read == 0)
                    throw new EndOfStreamException();
                totalRead += read;
            }

            return buffer;
        }

        /// <summary>
        /// Dosyayı satır satır async olarak oku
        /// </summary>
        public static async IAsyncEnumerable<string> ReadLinesAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line != null)
                    yield return line;
            }
        }

        /// <summary>
        /// JSON dosyasını async olarak oku ve deserialize et
        /// </summary>
        public static async Task<T?> ReadJsonAsync<T>(
            string path,
            JsonSerializerOptions? options = null,
            CancellationToken ct = default)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            return await JsonSerializer.DeserializeAsync<T>(stream, options, ct);
        }

        #endregion

        #region Write Operations

        /// <summary>
        /// Dosyaya async olarak yaz
        /// </summary>
        public static async Task WriteAllTextAsync(
            string path,
            string content,
            CancellationToken ct = default)
        {
            // Dizin yoksa oluştur
            EnsureDirectoryExists(path);

            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                AsyncOptions);

            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(content.AsMemory(), ct);
        }

        /// <summary>
        /// Dosyaya async olarak byte array yaz
        /// </summary>
        public static async Task WriteAllBytesAsync(
            string path,
            byte[] bytes,
            CancellationToken ct = default)
        {
            EnsureDirectoryExists(path);

            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                AsyncOptions);

            await stream.WriteAsync(bytes, ct);
        }

        /// <summary>
        /// Dosyaya async olarak satır ekle
        /// </summary>
        public static async Task AppendLineAsync(
            string path,
            string line,
            CancellationToken ct = default)
        {
            EnsureDirectoryExists(path);

            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }

        /// <summary>
        /// JSON olarak async yaz
        /// </summary>
        public static async Task WriteJsonAsync<T>(
            string path,
            T value,
            JsonSerializerOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureDirectoryExists(path);

            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                AsyncOptions);

            await JsonSerializer.SerializeAsync(stream, value, options, ct);
        }

        /// <summary>
        /// Atomic write - önce temp dosyaya yaz, sonra rename et
        /// </summary>
        public static async Task WriteAllTextAtomicAsync(
            string path,
            string content,
            CancellationToken ct = default)
        {
            EnsureDirectoryExists(path);

            var tempPath = path + ".tmp";

            try
            {
                await WriteAllTextAsync(tempPath, content, ct);

                // Atomic rename
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
            }
            catch
            {
                // Cleanup temp file on error
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                throw;
            }
        }

        #endregion

        #region Stream Operations

        /// <summary>
        /// Stream'i async olarak kopyala
        /// </summary>
        public static async Task CopyStreamAsync(
            Stream source,
            Stream destination,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
            try
            {
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, DefaultBufferSize), ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    progress?.Report(totalRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Dosyayı async olarak kopyala
        /// </summary>
        public static async Task CopyFileAsync(
            string sourcePath,
            string destPath,
            bool overwrite = false,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            if (!overwrite && File.Exists(destPath))
            {
                throw new IOException($"Destination file already exists: {destPath}");
            }

            EnsureDirectoryExists(destPath);

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                AsyncOptions);

            await using var dest = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                AsyncOptions);

            await CopyStreamAsync(source, dest, progress, ct);
        }

        #endregion

        #region Directory Operations

        /// <summary>
        /// Dizin yoksa oluştur
        /// </summary>
        public static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        /// <summary>
        /// Dizini async olarak temizle (eski dosyaları sil)
        /// </summary>
        public static async Task CleanupDirectoryAsync(
            string path,
            TimeSpan maxAge,
            string searchPattern = "*",
            CancellationToken ct = default)
        {
            if (!Directory.Exists(path)) return;

            var cutoff = DateTime.UtcNow - maxAge;
            var files = Directory.GetFiles(path, searchPattern);

            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            File.Delete(file);
                            Log.Debug("[FileHelper] Eski dosya silindi: {File}", file);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[FileHelper] Dosya silinemedi: {File}", file);
                    }
                }
            }, ct);
        }

        /// <summary>
        /// Dizin boyutunu hesapla
        /// </summary>
        public static async Task<long> GetDirectorySizeAsync(
            string path,
            string searchPattern = "*",
            SearchOption searchOption = SearchOption.AllDirectories,
            CancellationToken ct = default)
        {
            if (!Directory.Exists(path)) return 0;

            return await Task.Run(() =>
            {
                long size = 0;
                var files = Directory.GetFiles(path, searchPattern, searchOption);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        size += info.Length;
                    }
                    catch { }
                }

                return size;
            }, ct);
        }

        #endregion

        #region Safe Operations

        /// <summary>
        /// Güvenli dosya okuma - hata durumunda default değer döner
        /// </summary>
        public static async Task<string?> TryReadAllTextAsync(
            string path,
            CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return await ReadAllTextAsync(path, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FileHelper] Dosya okunamadı: {Path}", path);
                return null;
            }
        }

        /// <summary>
        /// Güvenli JSON okuma
        /// </summary>
        public static async Task<T?> TryReadJsonAsync<T>(
            string path,
            JsonSerializerOptions? options = null,
            CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(path)) return default;
                return await ReadJsonAsync<T>(path, options, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FileHelper] JSON okunamadı: {Path}", path);
                return default;
            }
        }

        /// <summary>
        /// Güvenli dosya yazma
        /// </summary>
        public static async Task<bool> TryWriteAllTextAsync(
            string path,
            string content,
            CancellationToken ct = default)
        {
            try
            {
                await WriteAllTextAsync(path, content, ct);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FileHelper] Dosya yazılamadı: {Path}", path);
                return false;
            }
        }

        #endregion
    }
}
