using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;
using System.Net.Http;
using UniCast.LicenseServer.Models;
using UniCast.LicenseServer.Services;

// Serilog konfigürasyonu
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine("logs", "license-server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("═══════════════════════════════════════════════════════");
    Log.Information("UniCast License Server Başlatılıyor...");
    Log.Information("═══════════════════════════════════════════════════════");

    var builder = WebApplication.CreateBuilder(args);

    // Serilog'u ekle
    builder.Host.UseSerilog();

    // Services
    builder.Services.AddSingleton<ILicenseService, LicenseService>();
    builder.Services.AddSingleton<ILicenseRepository, LicenseRepository>();

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // Rate limiting (basit)
    builder.Services.AddSingleton<RateLimiter>();

    var app = builder.Build();

    // Middleware
    app.UseSerilogRequestLogging();
    app.UseCors();

    // Health check
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

    // API versiyonu
    app.MapGet("/api/v1", () => Results.Ok(new
    {
        name = "UniCast License Server",
        version = "1.0.0",
        timestamp = DateTime.UtcNow
    }));

    // Lisans aktivasyonu
    app.MapPost("/api/v1/activate", async (
        ActivationRequest request,
        ILicenseService licenseService,
        RateLimiter rateLimiter,
        HttpContext context) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Rate limiting
        if (!rateLimiter.TryAcquire(clientIp, "activate"))
        {
            Log.Warning("[Activate] Rate limit aşıldı: {IP}", clientIp);
            return Results.StatusCode(429); // Too Many Requests
        }

        Log.Information("[Activate] İstek: Key={Key}, HW={HW}, Machine={Machine}",
            MaskLicenseKey(request.LicenseKey),
            request.HardwareIdShort,
            request.MachineName);

        try
        {
            var result = await licenseService.ActivateAsync(request, clientIp);

            if (result.Success)
            {
                Log.Information("[Activate] Başarılı: {LicenseId}", result.License?.LicenseId);
                return Results.Ok(result);
            }
            else
            {
                Log.Warning("[Activate] Başarısız: {Message}", result.Message);
                return Results.BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Activate] Hata");
            return Results.Problem("Internal server error");
        }
    });

    // Lisans deaktivasyonu
    app.MapPost("/api/v1/deactivate", async (
        DeactivationRequest request,
        ILicenseService licenseService,
        RateLimiter rateLimiter,
        HttpContext context) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!rateLimiter.TryAcquire(clientIp, "deactivate"))
        {
            return Results.StatusCode(429);
        }

        Log.Information("[Deactivate] İstek: LicenseId={Id}, HW={HW}",
            request.LicenseId, request.HardwareId?[..Math.Min(16, request.HardwareId.Length)]);

        try
        {
            var success = await licenseService.DeactivateAsync(request);

            if (success)
            {
                Log.Information("[Deactivate] Başarılı");
                return Results.Ok(new { success = true });
            }
            else
            {
                Log.Warning("[Deactivate] Başarısız");
                return Results.BadRequest(new { success = false, message = "Deactivation failed" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Deactivate] Hata");
            return Results.Problem("Internal server error");
        }
    });

    // Online doğrulama
    app.MapPost("/api/v1/validate", async (
        ValidationRequest request,
        ILicenseService licenseService,
        RateLimiter rateLimiter,
        HttpContext context) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!rateLimiter.TryAcquire(clientIp, "validate"))
        {
            return Results.StatusCode(429);
        }

        Log.Debug("[Validate] İstek: LicenseId={Id}", request.LicenseId);

        try
        {
            var result = await licenseService.ValidateAsync(request);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Validate] Hata");
            return Results.Problem("Internal server error");
        }
    });

    // Admin: Lisans oluşturma (güvenli endpoint - production'da auth ekleyin)
    app.MapPost("/api/v1/admin/create", async (
        CreateLicenseRequest request,
        ILicenseService licenseService,
        HttpContext context) =>
    {
        // TODO: Admin authentication ekleyin!
        var adminKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();

        if (adminKey != Environment.GetEnvironmentVariable("ADMIN_KEY"))
        {
            return Results.Unauthorized();
        }

        Log.Information("[Admin] Lisans oluşturma: Type={Type}, Email={Email}",
            request.Type, request.Email);

        try
        {
            var license = await licenseService.CreateLicenseAsync(request);
            return Results.Ok(new { success = true, license });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Admin] Lisans oluşturma hatası");
            return Results.Problem(ex.Message);
        }
    });

    // Admin: Lisans iptal
    app.MapPost("/api/v1/admin/revoke/{licenseId}", async (
        string licenseId,
        ILicenseService licenseService,
        HttpContext context) =>
    {
        var adminKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();

        if (adminKey != Environment.GetEnvironmentVariable("ADMIN_KEY"))
        {
            return Results.Unauthorized();
        }

        Log.Information("[Admin] Lisans iptal: {LicenseId}", licenseId);

        try
        {
            var success = await licenseService.RevokeLicenseAsync(licenseId);
            return success ? Results.Ok() : Results.NotFound();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Admin] Lisans iptal hatası");
            return Results.Problem(ex.Message);
        }
    });

    // Admin: Lisans listesi
    app.MapGet("/api/v1/admin/licenses", async (
        ILicenseService licenseService,
        HttpContext context) =>
    {
        var adminKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();

        if (adminKey != Environment.GetEnvironmentVariable("ADMIN_KEY"))
        {
            return Results.Unauthorized();
        }

        try
        {
            var licenses = await licenseService.GetAllLicensesAsync();
            return Results.Ok(licenses);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Admin] Lisans listesi hatası");
            return Results.Problem(ex.Message);
        }
    });

    Log.Information("License Server dinleniyor: http://localhost:5000");
    app.Run("http://0.0.0.0:5000");
}
catch (Exception ex)
{
    Log.Fatal(ex, "License Server başlatılamadı");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Helper
static string MaskLicenseKey(string? key)
{
    if (string.IsNullOrEmpty(key) || key.Length < 10)
        return "***";

    return key[..5] + "-***-" + key[^5..];
}

// Request/Response modelleri (ayrı dosyada da olabilir)
namespace UniCast.LicenseServer.Models
{
    public record ActivationRequest(
        string LicenseKey,
        string HardwareId,
        string HardwareIdShort,
        string MachineName,
        string ComponentsHash,
        string OsVersion,
        string AppVersion);

    public record DeactivationRequest(
        string LicenseId,
        string LicenseKey,
        string HardwareId);

    public record ValidationRequest(
        string LicenseId,
        string LicenseKey,
        string HardwareId,
        string AppVersion);

    public record CreateLicenseRequest(
        string Type,
        string Email,
        string Name,
        int DurationDays,
        int MaxMachines);
}