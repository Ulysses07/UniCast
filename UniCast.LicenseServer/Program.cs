using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Threading.RateLimiting;
using UniCast.LicenseServer.Services;
using RateLimiter = UniCast.LicenseServer.Services.RateLimiter;

// Serilog yapılandırması
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("Logs/license-server-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("UniCast License Server başlatılıyor...");

    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog();

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

    // Services
    builder.Services.AddSingleton<ILicenseRepository, LicenseRepository>();
    builder.Services.AddSingleton<ILicenseService, LicenseService>();
    builder.Services.AddSingleton<RateLimiter>();

    var app = builder.Build();

    // Middleware
    app.UseCors();
    app.UseSerilogRequestLogging();

    // Rate limiter instance
    var rateLimiter = app.Services.GetRequiredService<RateLimiter>();
    var licenseService = app.Services.GetRequiredService<ILicenseService>();

    // Admin key
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "default-admin-key-change-me";

    // Health check
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

    // API version
    app.MapGet("/api/v1", () => Results.Ok(new { version = "1.0", name = "UniCast License Server" }));

    // Activate license
    app.MapPost("/api/v1/activate", async (HttpContext context, ActivationRequest request) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!rateLimiter.TryAcquire(clientIp, "activate"))
        {
            return Results.StatusCode(429);
        }

        try
        {
            var result = await licenseService.ActivateAsync(
                request.LicenseKey,
                request.HardwareId,
                request.MachineName,
                clientIp);

            if (result.Success)
            {
                return Results.Ok(new
                {
                    success = true,
                    license = result.License
                });
            }

            return Results.BadRequest(new { success = false, error = result.ErrorMessage });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Activation error");
            return Results.StatusCode(500);
        }
    });

    // Deactivate license
    app.MapPost("/api/v1/deactivate", async (HttpContext context, DeactivationRequest request) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!rateLimiter.TryAcquire(clientIp, "deactivate"))
        {
            return Results.StatusCode(429);
        }

        try
        {
            var result = await licenseService.DeactivateAsync(request.LicenseKey, request.HardwareId);
            return result
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { success = false, error = "Deactivation failed" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deactivation error");
            return Results.StatusCode(500);
        }
    });

    // Validate license
    app.MapPost("/api/v1/validate", async (HttpContext context, ValidationRequest request) =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!rateLimiter.TryAcquire(clientIp, "validate"))
        {
            return Results.StatusCode(429);
        }

        try
        {
            var result = await licenseService.ValidateAsync(request.LicenseKey, request.HardwareId);
            return Results.Ok(new
            {
                valid = result.IsValid,
                error = result.ErrorMessage,
                license = result.License
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Validation error");
            return Results.StatusCode(500);
        }
    });

    // Admin: Create license
    app.MapPost("/api/v1/admin/create", async (HttpContext context, CreateLicenseRequest request) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
        {
            return Results.Unauthorized();
        }

        try
        {
            var license = await licenseService.CreateLicenseAsync(request);
            return Results.Ok(new { success = true, license });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Create license error");
            return Results.StatusCode(500);
        }
    });

    // Admin: Revoke license
    app.MapPost("/api/v1/admin/revoke/{licenseId}", async (HttpContext context, string licenseId) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await licenseService.RevokeLicenseAsync(licenseId);
            return result
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { success = false, error = "License not found" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Revoke license error");
            return Results.StatusCode(500);
        }
    });

    // Admin: List licenses
    app.MapGet("/api/v1/admin/licenses", async (HttpContext context) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
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
            Log.Error(ex, "List licenses error");
            return Results.StatusCode(500);
        }
    });

    // Admin: Get license details
    app.MapGet("/api/v1/admin/licenses/{licenseId}", async (HttpContext context, string licenseId) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
        {
            return Results.Unauthorized();
        }

        try
        {
            var license = await licenseService.GetLicenseByIdAsync(licenseId);
            return license != null
                ? Results.Ok(license)
                : Results.NotFound(new { error = "License not found" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Get license error");
            return Results.StatusCode(500);
        }
    });

    // Admin: Renew support period
    app.MapPost("/api/v1/admin/renew-support", async (HttpContext context, RenewSupportRequest request) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await licenseService.RenewSupportAsync(request.LicenseId, request.DurationDays);
            return result
                ? Results.Ok(new { success = true, message = $"Support renewed for {request.DurationDays} days" })
                : Results.NotFound(new { success = false, error = "License not found" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Renew support error");
            return Results.StatusCode(500);
        }
    });

    // Admin: Unrevoke license (restore)
    app.MapPost("/api/v1/admin/unrevoke/{licenseId}", async (HttpContext context, string licenseId) =>
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (providedKey != adminKey)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await licenseService.UnrevokeLicenseAsync(licenseId);
            return result
                ? Results.Ok(new { success = true, message = "License restored" })
                : Results.NotFound(new { success = false, error = "License not found" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unrevoke license error");
            return Results.StatusCode(500);
        }
    });

    Log.Information("Server listening on http://0.0.0.0:5000");
    app.Run("http://0.0.0.0:5000");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Request models
public record ActivationRequest(string LicenseKey, string HardwareId, string? MachineName);
public record DeactivationRequest(string LicenseKey, string HardwareId);
public record ValidationRequest(string LicenseKey, string HardwareId);
public record CreateLicenseRequest(
    string LicenseeName,
    string LicenseeEmail,
    int MaxMachines = 1,
    int SupportDurationDays = 365  // Destek süresi (varsayılan 1 yıl)
);
public record RenewSupportRequest(string LicenseId, int DurationDays = 365);