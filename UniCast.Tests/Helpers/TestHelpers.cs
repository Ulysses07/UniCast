using System.IO;

namespace UniCast.Tests.Helpers;

/// <summary>
/// Tüm testler için temel sınıf
/// </summary>
public abstract class TestBase : IDisposable
{
    protected readonly CancellationTokenSource Cts;
    protected CancellationToken CancellationToken => Cts.Token;

    protected TestBase()
    {
        Cts = new CancellationTokenSource();
    }

    public virtual void Dispose()
    {
        Cts.Cancel();
        Cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Async testler için temel sınıf
/// </summary>
public abstract class AsyncTestBase : TestBase
{
    protected async Task WithTimeoutAsync(Func<Task> action, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await action();
    }

    protected async Task<T> WithTimeoutAsync<T>(Func<Task<T>> action, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await action();
    }
}

/// <summary>
/// Geçici dosya yönetimi için helper
/// </summary>
public sealed class TempFileFixture : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public string CreateTempFile(string extension = ".tmp", string? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"unicast_test_{Guid.NewGuid()}{extension}");
        
        if (content != null)
            File.WriteAllText(path, content);
        else
            File.Create(path).Dispose();
        
        _tempFiles.Add(path);
        return path;
    }

    public string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unicast_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* ignore cleanup errors */ }
        }

        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* ignore cleanup errors */ }
        }
    }
}

/// <summary>
/// Mock factory helper
/// </summary>
public static class MockFactory
{
    /// <summary>
    /// Standart CancellationToken oluştur
    /// </summary>
    public static CancellationToken CreateCancellationToken(int timeoutMs = 5000)
    {
        return new CancellationTokenSource(timeoutMs).Token;
    }

    /// <summary>
    /// İptal edilmiş CancellationToken oluştur
    /// </summary>
    public static CancellationToken CreateCancelledToken()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }
}

/// <summary>
/// Test data builder pattern
/// </summary>
public static class TestDataBuilder
{
    public static string RandomString(int length = 10)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    public static string RandomEmail()
    {
        return $"{RandomString(8)}@{RandomString(5)}.com";
    }

    public static string RandomLicenseKey()
    {
        return $"UC-{RandomString(4)}-{RandomString(4)}-{RandomString(4)}-{RandomString(4)}".ToUpper();
    }
}
