using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniCast.LicenseServer.Services;
using RateLimiter = UniCast.LicenseServer.Services.RateLimiter;

// ==================== CONSTANTS ====================
const string ApiVersion = "1.0.0";
const string ApiTitle = "UniCast License Server API";
const string CorrelationIdHeader = "X-Correlation-ID";
const string AdminKeyHeader = "X-Admin-Key";
const string RateLimitRemainingHeader = "X-RateLimit-Remaining";
const string RateLimitResetHeader = "X-RateLimit-Reset";

var startTime = DateTime.UtcNow;

// ==================== SERILOG CONFIGURATION ====================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "UniCast.LicenseServer")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("Logs/license-server-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50_000_000, // 50MB
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("UniCast License Server v{Version} başlatılıyor...", ApiVersion);

    var builder = WebApplication.CreateBuilder(args);

    // ==================== CONFIGURATION VALIDATION ====================
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrEmpty(adminKey) || adminKey.Length < 32)
    {
        Log.Fatal("KRİTİK: ADMIN_KEY environment variable ayarlanmalı (min 32 karakter)");
        Log.Fatal("Örnek: export ADMIN_KEY=$(openssl rand -hex 32)");
        Environment.Exit(1);
        return;
    }

    var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? new[] { "https://unicastapp.com", "https://license.unicastapp.com" };

    Log.Information("Konfigürasyon doğrulandı - Admin key: {Length} karakter, CORS origins: {Origins}",
        adminKey.Length, string.Join(", ", allowedOrigins));

    // ==================== SERVICE CONFIGURATION ====================
    builder.Host.UseSerilog();

    // JSON options
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders(CorrelationIdHeader, RateLimitRemainingHeader, RateLimitResetHeader);
        });

        options.AddPolicy("HealthCheck", policy =>
        {
            policy.AllowAnyOrigin()
                  .WithMethods("GET")
                  .AllowAnyHeader();
        });
    });

    // Response compression
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
    });

    // Problem Details (RFC 7807)
    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions["timestamp"] = DateTime.UtcNow;
        };
    });

    // Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = ApiTitle,
            Version = $"v{ApiVersion}",
            Description = """
                UniCast uygulaması için lisans yönetim API'si.
                
                ## Özellikler
                - ✅ Lisans aktivasyonu ve deaktivasyonu
                - ✅ Lisans doğrulama
                - ✅ Admin yönetim paneli
                
                ## Rate Limiting
                | Endpoint | Limit |
                |----------|-------|
                | Activate | 10/dakika |
                | Validate | 30/dakika |
                | Deactivate | 5/dakika |
                
                ## Güvenlik
                Admin endpoint'leri `X-Admin-Key` header'ı gerektirir.
                
                ## Response Headers
                - `X-Correlation-ID`: İstek takip ID'si
                - `X-RateLimit-Remaining`: Kalan istek hakkı
                - `X-RateLimit-Reset`: Rate limit sıfırlanma zamanı (ISO 8601)
                """,
            Contact = new Microsoft.OpenApi.OpenApiContact
            {
                Name = "UniCast Support",
                Email = "support@unicastapp.com",
                Url = new Uri("https://unicastapp.com")
            },
            License = new Microsoft.OpenApi.OpenApiLicense
            {
                Name = "Proprietary",
                Url = new Uri("https://unicastapp.com/license")
            }
        });

        // Security scheme
        options.AddSecurityDefinition("AdminKey", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Name = AdminKeyHeader,
            Description = "Admin API erişimi için gerekli anahtar"
        });

        // Apply security to admin endpoints
        options.OperationFilter<AdminSecurityOperationFilter>();

        // XML comments
        var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }

        // Tag grouping
        options.TagActionsBy(api =>
        {
            if (api.RelativePath == null) return new[] { "Other" };
            if (api.RelativePath.Contains("admin")) return new[] { "Admin" };
            if (api.RelativePath.Contains("health") || api.RelativePath == "api/v1") return new[] { "Health" };
            return new[] { "License" };
        });
    });

    // Application services
    builder.Services.AddSingleton<ILicenseRepository, LicenseRepository>();
    builder.Services.AddSingleton<ILicenseService, LicenseService>();
    builder.Services.AddSingleton<RateLimiter>();

    var app = builder.Build();

    // ==================== MIDDLEWARE PIPELINE ====================

    // 1. Correlation ID middleware (en başta)
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N")[..12];

        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next();
        }
    });

    // 2. Security headers
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next();
    });

    // 3. Response compression
    app.UseResponseCompression();

    // 4. Global exception handler
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exceptionHandler = context.Features.Get<IExceptionHandlerFeature>();
            var exception = exceptionHandler?.Error;

            Log.Error(exception, "Unhandled exception: {Message}", exception?.Message);

            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal Server Error",
                Detail = app.Environment.IsDevelopment() ? exception?.Message : "An unexpected error occurred. Please try again later.",
                Instance = context.Request.Path
            };
            problemDetails.Extensions["correlationId"] = context.TraceIdentifier;
            problemDetails.Extensions["timestamp"] = DateTime.UtcNow;

            await context.Response.WriteAsJsonAsync(problemDetails);
        });
    });

    // 5. Swagger
    app.UseSwagger(options =>
    {
        options.RouteTemplate = "swagger/{documentName}/swagger.json";
    });
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", $"{ApiTitle} v{ApiVersion}");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = $"{ApiTitle} - Documentation";
        options.DefaultModelsExpandDepth(2);
        options.EnableDeepLinking();
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();
    });

    // 6. CORS
    app.UseCors();

    // 7. Request logging
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("ClientIP", GetClientIp(httpContext));
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        };
    });

    // Services
    var rateLimiter = app.Services.GetRequiredService<RateLimiter>();
    var licenseService = app.Services.GetRequiredService<ILicenseService>();

    // ==================== HEALTH ENDPOINTS ====================

    var healthGroup = app.MapGroup("")
        .WithTags("Health")
        .RequireCors("HealthCheck");

    /// <summary>Liveness probe - sunucu çalışıyor mu?</summary>
    healthGroup.MapGet("/health", () =>
    {
        return Results.Ok(new HealthResponse(
            Status: "healthy",
            Version: ApiVersion,
            Timestamp: DateTime.UtcNow,
            Uptime: GetUptime(startTime)
        ));
    })
    .WithName("HealthCheck")
    .WithDescription("Sunucu sağlık kontrolü (liveness probe)")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

    /// <summary>Readiness probe - sunucu isteklere hazır mı?</summary>
    healthGroup.MapGet("/health/ready", async (ILicenseRepository repo) =>
    {
        var checks = new Dictionary<string, HealthCheckResult>();

        // Database check
        try
        {
            var dbHealthy = await repo.HealthCheckAsync();
            checks["database"] = new HealthCheckResult(dbHealthy ? "healthy" : "unhealthy", null);
        }
        catch (Exception ex)
        {
            checks["database"] = new HealthCheckResult("unhealthy", ex.Message);
        }

        // Memory check
        var memoryMb = GC.GetTotalMemory(false) / 1024 / 1024;
        checks["memory"] = new HealthCheckResult(memoryMb < 500 ? "healthy" : "degraded", $"{memoryMb}MB");

        var allHealthy = checks.Values.All(c => c.Status == "healthy");

        return allHealthy
            ? Results.Ok(new ReadinessResponse(true, checks))
            : Results.Json(new ReadinessResponse(false, checks), statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("ReadinessCheck")
    .WithDescription("Sunucu hazırlık kontrolü (readiness probe)")
    .Produces<ReadinessResponse>(StatusCodes.Status200OK)
    .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

    /// <summary>API bilgisi</summary>
    app.MapGet("/api/v1", () => Results.Ok(new ApiInfoResponse(
        Name: "UniCast License Server",
        Version: ApiVersion,
        Documentation: "/swagger",
        Health: "/health"
    )))
    .WithName("GetApiInfo")
    .WithTags("Health")
    .WithDescription("API bilgisi ve versiyon")
    .Produces<ApiInfoResponse>(StatusCodes.Status200OK);

    // ==================== CONFIG ENDPOINTS ====================
    // Platform selector'ları ve diğer dinamik konfigürasyonlar
    // Bu endpoint'ler public - rate limiting uygulanıyor

    var configGroup = app.MapGroup("/api/v1/config")
        .WithTags("Config")
        .RequireCors("HealthCheck"); // Public erişim

    /// <summary>Instagram selector konfigürasyonu</summary>
    configGroup.MapGet("/instagram-selectors", (HttpContext context) =>
    {
        var clientIp = GetClientIp(context);

        // Rate limiting - saatte 60 istek
        if (!rateLimiter.TryAcquire(clientIp, "config", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        // Selector konfigürasyonu
        // Instagram class'ları değiştiğinde sadece burayı güncelle!
        var config = new InstagramSelectorConfig
        {
            Version = 1,
            UpdatedAt = new DateTime(2025, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            Selectors = new InstagramSelectors
            {
                // Test edilmiş selector'lar (12 Ocak 2025)
                Username = "span._ap3a._aaco._aacw._aacx._aad7",
                Message = "span._ap3a._aaco._aacu._aacx._aad7._aadf",
                // Yayıncı adı (filtreleme için)
                Broadcaster = "span._ap3a._aaco._aacw._aacx._aada"
            },
            // Alternatif selector'lar (fallback)
            FallbackSelectors = new InstagramSelectors
            {
                Username = "span[dir='auto']",
                Message = "span[dir='auto']",
                Broadcaster = null
            },
            // Polling ayarları
            PollingIntervalMs = 3000,
            // Not
            Notes = "Instagram Live Chat için DOM selector'ları. Class'lar değişirse bu config güncellenir."
        };

        Log.Debug("Instagram selectors requested from {IP}", clientIp);
        return Results.Ok(config);
    })
    .WithName("GetInstagramSelectors")
    .WithDescription("Instagram Live Chat için DOM selector konfigürasyonu")
    .Produces<InstagramSelectorConfig>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>Facebook selector konfigürasyonu</summary>
    configGroup.MapGet("/facebook-selectors", (HttpContext context) =>
    {
        var clientIp = GetClientIp(context);

        if (!rateLimiter.TryAcquire(clientIp, "config", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        var config = new FacebookSelectorConfig
        {
            Version = 1,
            UpdatedAt = new DateTime(2025, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            Selectors = new FacebookSelectors
            {
                CommentContainer = "div.xv55zj0.x1vvkbs",
                AuthorLink = "a",
                CommentText = "div[dir='auto']"
            },
            FallbackSelectors = new FacebookSelectors
            {
                CommentContainer = "div[role='article']",
                AuthorLink = "a[role='link']",
                CommentText = "span"
            },
            PollingIntervalMs = 5000,
            Notes = "Facebook Live Chat için DOM selector'ları."
        };

        Log.Debug("Facebook selectors requested from {IP}", clientIp);
        return Results.Ok(config);
    })
    .WithName("GetFacebookSelectors")
    .WithDescription("Facebook Live Chat için DOM selector konfigürasyonu")
    .Produces<FacebookSelectorConfig>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>Tüm platform selector'larını getir</summary>
    configGroup.MapGet("/selectors", (HttpContext context) =>
    {
        var clientIp = GetClientIp(context);

        if (!rateLimiter.TryAcquire(clientIp, "config", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        var config = new AllSelectorsConfig
        {
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
            Instagram = new InstagramSelectors
            {
                Username = "span._ap3a._aaco._aacw._aacx._aad7",
                Message = "span._ap3a._aaco._aacu._aacx._aad7._aadf",
                Broadcaster = "span._ap3a._aaco._aacw._aacx._aada"
            },
            Facebook = new FacebookSelectors
            {
                CommentContainer = "div.xv55zj0.x1vvkbs",
                AuthorLink = "a",
                CommentText = "div[dir='auto']"
            }
        };

        Log.Debug("All selectors requested from {IP}", clientIp);
        return Results.Ok(config);
    })
    .WithName("GetAllSelectors")
    .WithDescription("Tüm platformlar için DOM selector konfigürasyonları")
    .Produces<AllSelectorsConfig>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    // ==================== LICENSE ENDPOINTS ====================

    var licenseGroup = app.MapGroup("/api/v1")
        .WithTags("License");

    /// <summary>Lisans aktivasyonu</summary>
    licenseGroup.MapPost("/activate", async (HttpContext context, ActivationRequest request) =>
    {
        // Validation
        var validationError = ValidateActivationRequest(request);
        if (validationError != null)
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", validationError));

        var clientIp = GetClientIp(context);

        // Rate limiting
        if (!rateLimiter.TryAcquire(clientIp, "activate", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        var result = await licenseService.ActivateAsync(
            request.LicenseKey,
            request.HardwareId,
            request.MachineName,
            clientIp);

        if (result.Success)
        {
            Log.Information("License activated: {LicenseKey} for {HardwareId} from {IP}",
                MaskLicenseKey(request.LicenseKey), MaskId(request.HardwareId), clientIp);
            return Results.Ok(new ActivationResponse(true, result.License, null));
        }

        Log.Warning("Activation failed: {LicenseKey} - {Error}",
            MaskLicenseKey(request.LicenseKey), result.ErrorMessage);
        return Results.BadRequest(new ActivationResponse(false, null, result.ErrorMessage));
    })
    .WithName("ActivateLicense")
    .WithDescription("Lisans anahtarını belirtilen donanım için aktive eder")
    .Accepts<ActivationRequest>("application/json")
    .Produces<ActivationResponse>(StatusCodes.Status200OK)
    .Produces<ActivationResponse>(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>Lisans deaktivasyonu</summary>
    licenseGroup.MapPost("/deactivate", async (HttpContext context, DeactivationRequest request) =>
    {
        if (string.IsNullOrWhiteSpace(request.LicenseKey))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "LicenseKey is required"));
        if (string.IsNullOrWhiteSpace(request.HardwareId))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "HardwareId is required"));

        var clientIp = GetClientIp(context);

        if (!rateLimiter.TryAcquire(clientIp, "deactivate", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        var result = await licenseService.DeactivateAsync(request.LicenseKey, request.HardwareId);

        if (result)
        {
            Log.Information("License deactivated: {LicenseKey} from {HardwareId}",
                MaskLicenseKey(request.LicenseKey), MaskId(request.HardwareId));
            return Results.Ok(new SuccessResponse(true, "License deactivated successfully"));
        }

        return Results.BadRequest(new ErrorResponse("DEACTIVATION_FAILED", "Deactivation failed. License may not exist or hardware not registered."));
    })
    .WithName("DeactivateLicense")
    .WithDescription("Belirtilen donanımdan lisansı kaldırır")
    .Accepts<DeactivationRequest>("application/json")
    .Produces<SuccessResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>Lisans doğrulama</summary>
    licenseGroup.MapPost("/validate", async (HttpContext context, ValidationRequest request) =>
    {
        if (string.IsNullOrWhiteSpace(request.LicenseKey))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "LicenseKey is required"));
        if (string.IsNullOrWhiteSpace(request.HardwareId))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "HardwareId is required"));

        var clientIp = GetClientIp(context);

        if (!rateLimiter.TryAcquire(clientIp, "validate", out var remaining, out var resetTime))
        {
            AddRateLimitHeaders(context, remaining, resetTime);
            return CreateRateLimitResponse(context);
        }
        AddRateLimitHeaders(context, remaining, resetTime);

        var result = await licenseService.ValidateAsync(request.LicenseKey, request.HardwareId);

        return Results.Ok(new ValidationResponse(
            Valid: result.IsValid,
            License: result.License,
            Error: result.ErrorMessage,
            ValidatedAt: DateTime.UtcNow
        ));
    })
    .WithName("ValidateLicense")
    .WithDescription("Lisansın geçerliliğini kontrol eder")
    .Accepts<ValidationRequest>("application/json")
    .Produces<ValidationResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

    // ==================== ADMIN ENDPOINTS ====================

    var adminGroup = app.MapGroup("/api/v1/admin")
        .WithTags("Admin")
        .AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var providedKey = httpContext.Request.Headers[AdminKeyHeader].ToString();

            if (string.IsNullOrEmpty(providedKey))
            {
                Log.Warning("Admin access without key from {IP}", GetClientIp(httpContext));
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: "X-Admin-Key header is required"
                );
            }

            if (providedKey != adminKey)
            {
                Log.Warning("Invalid admin key from {IP}", GetClientIp(httpContext));
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: "Invalid admin key"
                );
            }

            return await next(context);
        });

    /// <summary>Yeni lisans oluştur</summary>
    adminGroup.MapPost("/licenses", async (CreateLicenseRequest request) =>
    {
        // Validation
        if (string.IsNullOrWhiteSpace(request.LicenseeName))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "LicenseeName is required"));
        if (string.IsNullOrWhiteSpace(request.LicenseeEmail))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "LicenseeEmail is required"));
        if (!request.LicenseeEmail.Contains('@'))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "Invalid email format"));
        if (request.MaxMachines < 1 || request.MaxMachines > 100)
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "MaxMachines must be between 1 and 100"));
        if (request.SupportDurationDays < 1 || request.SupportDurationDays > 3650)
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "SupportDurationDays must be between 1 and 3650"));

        var license = await licenseService.CreateLicenseAsync(request);
        Log.Information("License created for {Email} by admin", request.LicenseeEmail);

        return Results.Created($"/api/v1/admin/licenses/{license}", new CreateLicenseResponse(true, license));
    })
    .WithName("CreateLicense")
    .WithDescription("Yeni bir lisans oluşturur")
    .Accepts<CreateLicenseRequest>("application/json")
    .Produces<CreateLicenseResponse>(StatusCodes.Status201Created)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Tüm lisansları listele (pagination destekli)</summary>
    adminGroup.MapGet("/licenses", async (int? page, int? pageSize, string? status) =>
    {
        var allLicenses = await licenseService.GetAllLicensesAsync();

        // Optional status filter
        // Note: This would require the license object to have a Status property
        // For now, we return all licenses

        var total = allLicenses.Count();
        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var skip = (currentPage - 1) * size;

        var items = allLicenses.Skip(skip).Take(size);

        return Results.Ok(new PagedResponse<object>(
            Items: items,
            Page: currentPage,
            PageSize: size,
            TotalCount: total,
            TotalPages: (int)Math.Ceiling(total / (double)size)
        ));
    })
    .WithName("ListLicenses")
    .WithDescription("Tüm lisansları listeler (pagination destekli)")
    .Produces<PagedResponse<object>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Lisans detayı getir</summary>
    adminGroup.MapGet("/licenses/{licenseId}", async (string licenseId) =>
    {
        if (string.IsNullOrWhiteSpace(licenseId))
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "LicenseId is required"));

        var license = await licenseService.GetLicenseByIdAsync(licenseId);

        return license != null
            ? Results.Ok(license)
            : Results.NotFound(new ErrorResponse("NOT_FOUND", $"License '{licenseId}' not found"));
    })
    .WithName("GetLicenseDetails")
    .WithDescription("Belirtilen lisansın detaylarını getirir")
    .Produces<object>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Lisans iptal et (revoke)</summary>
    adminGroup.MapPost("/licenses/{licenseId}/revoke", async (string licenseId) =>
    {
        var result = await licenseService.RevokeLicenseAsync(licenseId);

        if (result)
        {
            Log.Information("License revoked by admin: {LicenseId}", licenseId);
            return Results.Ok(new SuccessResponse(true, "License revoked successfully"));
        }

        return Results.NotFound(new ErrorResponse("NOT_FOUND", $"License '{licenseId}' not found"));
    })
    .WithName("RevokeLicense")
    .WithDescription("Lisansı iptal eder (revoke)")
    .Produces<SuccessResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>İptal edilmiş lisansı geri yükle</summary>
    adminGroup.MapPost("/licenses/{licenseId}/restore", async (string licenseId) =>
    {
        var result = await licenseService.UnrevokeLicenseAsync(licenseId);

        if (result)
        {
            Log.Information("License restored by admin: {LicenseId}", licenseId);
            return Results.Ok(new SuccessResponse(true, "License restored successfully"));
        }

        return Results.NotFound(new ErrorResponse("NOT_FOUND", $"License '{licenseId}' not found"));
    })
    .WithName("RestoreLicense")
    .WithDescription("İptal edilmiş lisansı geri yükler")
    .Produces<SuccessResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>Destek süresini uzat</summary>
    adminGroup.MapPost("/licenses/{licenseId}/renew-support", async (string licenseId, RenewSupportRequest request) =>
    {
        if (request.DurationDays < 1 || request.DurationDays > 3650)
            return Results.BadRequest(new ErrorResponse("VALIDATION_ERROR", "DurationDays must be between 1 and 3650"));

        var result = await licenseService.RenewSupportAsync(licenseId, request.DurationDays);

        if (result)
        {
            Log.Information("Support renewed by admin: {LicenseId} for {Days} days", licenseId, request.DurationDays);
            return Results.Ok(new SuccessResponse(true, $"Support renewed for {request.DurationDays} days"));
        }

        return Results.NotFound(new ErrorResponse("NOT_FOUND", $"License '{licenseId}' not found"));
    })
    .WithName("RenewSupport")
    .WithDescription("Lisansın destek süresini uzatır")
    .Accepts<RenewSupportRequest>("application/json")
    .Produces<SuccessResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

    // ==================== SERVER STARTUP ====================

    var useHttps = Environment.GetEnvironmentVariable("USE_HTTPS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
    var httpsPort = Environment.GetEnvironmentVariable("HTTPS_PORT") ?? "5001";

    Log.Information("===========================================");
    Log.Information("  UniCast License Server v{Version}", ApiVersion);
    Log.Information("===========================================");
    Log.Information("Swagger UI: /swagger");
    Log.Information("Health: /health, /health/ready");
    Log.Information("API Info: /api/v1");

    if (useHttps)
    {
        var certPath = Environment.GetEnvironmentVariable("SSL_CERT_PATH");
        if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
        {
            Log.Fatal("KRİTİK: SSL_CERT_PATH ayarlanmalı veya dosya bulunamadı");
            Environment.Exit(1);
            return;
        }

        Log.Information("Mode: HTTPS (Production)");
        Log.Information("Ports: HTTPS={HttpsPort}, HTTP={HttpPort} (redirect)", httpsPort, port);

        app.UseHttpsRedirection();
        app.Urls.Clear();
        app.Urls.Add($"https://0.0.0.0:{httpsPort}");
        app.Urls.Add($"http://0.0.0.0:{port}");
    }
    else
    {
        Log.Warning("===========================================");
        Log.Warning("  ⚠️  HTTP MODE - DEVELOPMENT ONLY!");
        Log.Warning("  Set USE_HTTPS=true for production");
        Log.Warning("===========================================");
        Log.Information("Port: {Port}", port);

        app.Urls.Clear();
        app.Urls.Add($"http://0.0.0.0:{port}");
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ==================== HELPER METHODS ====================

static string GetClientIp(HttpContext context) =>
    context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim()
    ?? context.Request.Headers["X-Real-IP"].FirstOrDefault()
    ?? context.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";

static void AddRateLimitHeaders(HttpContext context, int remaining, DateTime resetTime)
{
    context.Response.Headers[RateLimitRemainingHeader] = remaining.ToString();
    context.Response.Headers[RateLimitResetHeader] = resetTime.ToString("O");
}

static IResult CreateRateLimitResponse(HttpContext context) =>
    Results.Problem(
        statusCode: StatusCodes.Status429TooManyRequests,
        title: "Too Many Requests",
        detail: "Rate limit exceeded. Please wait before making another request.",
        extensions: new Dictionary<string, object?>
        {
            ["correlationId"] = context.TraceIdentifier,
            ["retryAfter"] = context.Response.Headers[RateLimitResetHeader].ToString()
        }
    );

static string? ValidateActivationRequest(ActivationRequest request)
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey))
        return "LicenseKey is required";
    if (request.LicenseKey.Length < 10)
        return "LicenseKey format is invalid";
    if (string.IsNullOrWhiteSpace(request.HardwareId))
        return "HardwareId is required";
    if (request.HardwareId.Length < 8)
        return "HardwareId format is invalid";
    return null;
}

static string MaskLicenseKey(string key) =>
    key.Length > 8 ? $"{key[..4]}****{key[^4..]}" : "****";

static string MaskId(string id) =>
    id.Length > 8 ? $"{id[..4]}...{id[^4..]}" : "****";

static string GetUptime(DateTime startTime)
{
    var uptime = DateTime.UtcNow - startTime;
    return uptime.TotalDays >= 1
        ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
        : uptime.TotalHours >= 1
            ? $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s"
            : $"{uptime.Minutes}m {uptime.Seconds}s";
}

// ==================== SWAGGER OPERATION FILTER ====================

/// <summary>Admin endpoint'lerine security requirement ekler</summary>
public class AdminSecurityOperationFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    public void Apply(Microsoft.OpenApi.OpenApiOperation operation, Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath?.Contains("admin") == true)
        {
            var securitySchemeRef = new Microsoft.OpenApi.OpenApiSecuritySchemeReference("AdminKey");

            operation.Security = new List<Microsoft.OpenApi.OpenApiSecurityRequirement>
            {
                new Microsoft.OpenApi.OpenApiSecurityRequirement
                {
                    { securitySchemeRef, new List<string>() }
                }
            };
        }
    }
}

// ==================== REQUEST MODELS ====================

/// <summary>Lisans aktivasyon isteği</summary>
/// <param name="LicenseKey">Lisans anahtarı (XXXX-XXXX-XXXX-XXXX formatında)</param>
/// <param name="HardwareId">Donanım parmak izi (unique machine identifier)</param>
/// <param name="MachineName">Makine adı (opsiyonel, bilgi amaçlı)</param>
public record ActivationRequest(
    string LicenseKey,
    string HardwareId,
    string? MachineName = null
);

/// <summary>Lisans deaktivasyon isteği</summary>
/// <param name="LicenseKey">Lisans anahtarı</param>
/// <param name="HardwareId">Deaktive edilecek donanım ID'si</param>
public record DeactivationRequest(string LicenseKey, string HardwareId);

/// <summary>Lisans doğrulama isteği</summary>
/// <param name="LicenseKey">Lisans anahtarı</param>
/// <param name="HardwareId">Donanım parmak izi</param>
public record ValidationRequest(string LicenseKey, string HardwareId);

/// <summary>Yeni lisans oluşturma isteği</summary>
/// <param name="LicenseeName">Lisans sahibinin adı</param>
/// <param name="LicenseeEmail">Lisans sahibinin e-posta adresi</param>
/// <param name="MaxMachines">Maksimum makine sayısı (1-100, varsayılan: 1)</param>
/// <param name="SupportDurationDays">Destek süresi gün cinsinden (1-3650, varsayılan: 365)</param>
public record CreateLicenseRequest(
    string LicenseeName,
    string LicenseeEmail,
    int MaxMachines = 1,
    int SupportDurationDays = 365
);

/// <summary>Destek süresi yenileme isteği</summary>
/// <param name="DurationDays">Eklenecek gün sayısı (1-3650, varsayılan: 365)</param>
public record RenewSupportRequest(int DurationDays = 365);

// ==================== RESPONSE MODELS ====================

/// <summary>Sağlık kontrolü yanıtı (liveness)</summary>
/// <param name="Status">Durum (healthy/unhealthy)</param>
/// <param name="Version">API versiyonu</param>
/// <param name="Timestamp">Zaman damgası (UTC)</param>
/// <param name="Uptime">Çalışma süresi</param>
public record HealthResponse(string Status, string Version, DateTime Timestamp, string Uptime);

/// <summary>Health check sonucu</summary>
/// <param name="Status">Durum (healthy/unhealthy/degraded)</param>
/// <param name="Details">Detay bilgisi</param>
public record HealthCheckResult(string Status, string? Details);

/// <summary>Hazırlık kontrolü yanıtı (readiness)</summary>
/// <param name="Ready">Hazır mı?</param>
/// <param name="Checks">Alt kontroller</param>
public record ReadinessResponse(bool Ready, Dictionary<string, HealthCheckResult> Checks);

/// <summary>API bilgisi</summary>
/// <param name="Name">API adı</param>
/// <param name="Version">API versiyonu</param>
/// <param name="Documentation">Dokümantasyon URL'i</param>
/// <param name="Health">Health check URL'i</param>
public record ApiInfoResponse(string Name, string Version, string Documentation, string Health);

/// <summary>Aktivasyon yanıtı</summary>
/// <param name="Success">Başarılı mı?</param>
/// <param name="License">Lisans bilgisi (başarılı ise)</param>
/// <param name="Error">Hata mesajı (başarısız ise)</param>
public record ActivationResponse(bool Success, object? License, string? Error);

/// <summary>Doğrulama yanıtı</summary>
/// <param name="Valid">Geçerli mi?</param>
/// <param name="License">Lisans bilgisi</param>
/// <param name="Error">Hata mesajı (geçersiz ise)</param>
/// <param name="ValidatedAt">Doğrulama zamanı (UTC)</param>
public record ValidationResponse(bool Valid, object? License, string? Error, DateTime ValidatedAt);

/// <summary>Lisans oluşturma yanıtı</summary>
/// <param name="Success">Başarılı mı?</param>
/// <param name="License">Oluşturulan lisans</param>
public record CreateLicenseResponse(bool Success, object License);

/// <summary>Başarı yanıtı</summary>
/// <param name="Success">Başarılı mı?</param>
/// <param name="Message">Mesaj</param>
public record SuccessResponse(bool Success, string Message);

/// <summary>Hata yanıtı</summary>
/// <param name="Code">Hata kodu</param>
/// <param name="Message">Hata mesajı</param>
public record ErrorResponse(string Code, string Message);

/// <summary>Sayfalı yanıt</summary>
/// <typeparam name="T">Öğe tipi</typeparam>
/// <param name="Items">Öğeler</param>
/// <param name="Page">Mevcut sayfa</param>
/// <param name="PageSize">Sayfa boyutu</param>
/// <param name="TotalCount">Toplam öğe sayısı</param>
/// <param name="TotalPages">Toplam sayfa sayısı</param>
public record PagedResponse<T>(
    IEnumerable<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

// ==================== CONFIG MODELS ====================

/// <summary>Instagram selector'ları</summary>
public record InstagramSelectors
{
    /// <summary>Kullanıcı adı selector'ı</summary>
    public string? Username { get; init; }

    /// <summary>Mesaj metni selector'ı</summary>
    public string? Message { get; init; }

    /// <summary>Yayıncı adı selector'ı (filtreleme için)</summary>
    public string? Broadcaster { get; init; }
}

/// <summary>Instagram selector konfigürasyonu</summary>
public record InstagramSelectorConfig
{
    /// <summary>Konfigürasyon versiyonu</summary>
    public int Version { get; init; }

    /// <summary>Son güncelleme tarihi</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Ana selector'lar</summary>
    public InstagramSelectors Selectors { get; init; } = new();

    /// <summary>Fallback selector'lar (ana selector'lar çalışmazsa)</summary>
    public InstagramSelectors? FallbackSelectors { get; init; }

    /// <summary>Polling aralığı (ms)</summary>
    public int PollingIntervalMs { get; init; } = 3000;

    /// <summary>Notlar</summary>
    public string? Notes { get; init; }
}

/// <summary>Facebook selector'ları</summary>
public record FacebookSelectors
{
    /// <summary>Yorum container selector'ı</summary>
    public string? CommentContainer { get; init; }

    /// <summary>Yazar link selector'ı</summary>
    public string? AuthorLink { get; init; }

    /// <summary>Yorum metni selector'ı</summary>
    public string? CommentText { get; init; }
}

/// <summary>Facebook selector konfigürasyonu</summary>
public record FacebookSelectorConfig
{
    /// <summary>Konfigürasyon versiyonu</summary>
    public int Version { get; init; }

    /// <summary>Son güncelleme tarihi</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Ana selector'lar</summary>
    public FacebookSelectors Selectors { get; init; } = new();

    /// <summary>Fallback selector'lar</summary>
    public FacebookSelectors? FallbackSelectors { get; init; }

    /// <summary>Polling aralığı (ms)</summary>
    public int PollingIntervalMs { get; init; } = 5000;

    /// <summary>Notlar</summary>
    public string? Notes { get; init; }
}

/// <summary>Tüm platform selector'ları</summary>
public record AllSelectorsConfig
{
    /// <summary>Konfigürasyon versiyonu</summary>
    public int Version { get; init; }

    /// <summary>Son güncelleme tarihi</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Instagram selector'ları</summary>
    public InstagramSelectors? Instagram { get; init; }

    /// <summary>Facebook selector'ları</summary>
    public FacebookSelectors? Facebook { get; init; }
}