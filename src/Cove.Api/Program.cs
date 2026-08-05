using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Core.Entities.Galleries;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;

// Ensure enough threads for async I/O under concurrent load
ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 4);

static async Task EnsurePostgresMigrationInfrastructureAsync(CoveContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        await conn.OpenAsync();

    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS public;
            CREATE TABLE IF NOT EXISTS public."__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }
    finally
    {
        if (shouldClose)
            await conn.CloseAsync();
    }
}

static bool IsWeakJwtSecret(string? secret)
    => string.IsNullOrWhiteSpace(secret)
        || secret == "CHANGE_ME_TO_A_RANDOM_SECRET_AT_LEAST_32_CHARS"
        || Encoding.UTF8.GetByteCount(secret) < 32;

static string GenerateJwtSecret()
    => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

static LogEventLevel ParseSerilogLogLevel(string? level)
    => level?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "critical" or "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

static string GetApplicationLogFilePath()
{
    var logDir = Path.Combine(CoveDefaultPaths.GetDataRoot(), "logs");
    Directory.CreateDirectory(logDir);
    return Path.Combine(logDir, "cove-.log");
}

// Verify the config/data directory is writeable BEFORE the logging pipeline is
// built. The very first thing startup does is create a log file inside this
// directory, so if it is read-only (a common Docker bind-mount uid mismatch)
// the process would otherwise die during logger construction with no log output
// at all. Emit a clear, actionable error to stderr and exit cleanly instead.
static void EnsureDataRootWriteable()
{
    var dataRoot = CoveDefaultPaths.GetDataRoot();
    try
    {
        Directory.CreateDirectory(dataRoot);
        var probePath = Path.Combine(dataRoot, ".cove-write-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probePath, string.Empty);
        File.Delete(probePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"FATAL: Cove cannot write to its config/data directory '{dataRoot}' (set via COVE_HOME).");
        Console.Error.WriteLine(
            "In Docker this usually means the mounted volume is owned by a different user than the one Cove runs as ('cove', uid 1000).");
        Console.Error.WriteLine(
            "Fix the host directory permissions, e.g. 'chown -R 1000:1000 ./cove-data', or run the container with a matching --user/PUID.");
        Console.Error.WriteLine($"Underlying error: {ex.GetType().Name}: {ex.Message}");
        Environment.Exit(1);
    }
}

static async Task<bool> HasPostgresApplicationTablesAsync(CoveContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        await conn.OpenAsync();

    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory'
            LIMIT 1
            """;
        return await cmd.ExecuteScalarAsync() != null;
    }
    finally
    {
        if (shouldClose)
            await conn.CloseAsync();
    }
}

static async Task<bool> HasPostgresColumnAsync(CoveContext db, string tableName, string columnName)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        await conn.OpenAsync();

    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
              AND column_name = @columnName
            LIMIT 1
            """;

        var tableParameter = cmd.CreateParameter();
        tableParameter.ParameterName = "tableName";
        tableParameter.Value = tableName;
        cmd.Parameters.Add(tableParameter);

        var columnParameter = cmd.CreateParameter();
        columnParameter.ParameterName = "columnName";
        columnParameter.Value = columnName;
        cmd.Parameters.Add(columnParameter);

        return await cmd.ExecuteScalarAsync() != null;
    }
    finally
    {
        if (shouldClose)
            await conn.CloseAsync();
    }
}

static async Task<bool> HasPostgresTableAsync(CoveContext db, string tableName)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        await conn.OpenAsync();

    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name = @tableName
            LIMIT 1
            """;

        var tableParameter = cmd.CreateParameter();
        tableParameter.ParameterName = "tableName";
        tableParameter.Value = tableName;
        cmd.Parameters.Add(tableParameter);

        return await cmd.ExecuteScalarAsync() != null;
    }
    finally
    {
        if (shouldClose)
            await conn.CloseAsync();
    }
}

try
{
    var builder = WebApplication.CreateBuilder(args);
    var isIntegrationTest = builder.Environment.IsEnvironment("IntegrationTest");
    var isIntegrationStartupTest = builder.Environment.IsEnvironment("IntegrationStartup");
    var isTestHarness = isIntegrationTest || isIntegrationStartupTest;

    var runtimeLogLevelManager = new RuntimeLogLevelManager(
        ParseSerilogLogLevel(builder.Configuration.GetValue<string>("Cove:LogLevel")),
        traceExpired: restoredLevel => Log
            .ForContext<RuntimeLogLevelManager>()
            .Information(
                "Temporary Trace logging expired; restored {LogLevel}",
                restoredLevel));
    builder.Services.AddSingleton(runtimeLogLevelManager);

    if (isTestHarness)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateLogger();
    }
    else
    {
        EnsureDataRootWriteable();

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(CoveTextLogFormatter.Instance)
            .WriteTo.File(CoveTextLogFormatter.Instance, GetApplicationLogFilePath(), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true)
            .CreateBootstrapLogger();

        // Serilog
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .MinimumLevel.ControlledBy(services.GetRequiredService<RuntimeLogLevelManager>().LevelSwitch)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console(CoveTextLogFormatter.Instance)
            .WriteTo.File(CoveTextLogFormatter.Instance, GetApplicationLogFilePath(), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true)
            .WriteTo.Sink(new SignalRLogSink()));
    }

    // Bind configuration
    var coveConfig = builder.Configuration.GetSection("Cove");
    builder.Services.Configure<CoveConfiguration>(coveConfig);
    builder.Services.Configure<AuthConfig>(coveConfig.GetSection("Auth"));
    builder.Services.Configure<PostgresConfig>(coveConfig.GetSection("Postgres"));

    // Register a singleton CoveConfiguration instance so all consumers share the same mutable object
    var coveCfgInstance = coveConfig.Get<CoveConfiguration>() ?? new CoveConfiguration();
    if (IsWeakJwtSecret(coveCfgInstance.Auth.JwtSecret))
        coveCfgInstance.Auth.JwtSecret = GenerateJwtSecret();
    coveCfgInstance.GeneratedPath = CoveDefaultPaths.ResolveDataPath(coveCfgInstance.GeneratedPath);
    coveCfgInstance.CachePath = CoveDefaultPaths.ResolveDataPath(coveCfgInstance.CachePath);
    coveCfgInstance.ExtensionPaths = coveCfgInstance.ExtensionPaths
        .Select(CoveDefaultPaths.ResolveDataPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    builder.Services.AddSingleton(coveCfgInstance);

    // Database - EF Core + PostgreSQL
    var pgSection = coveConfig.GetSection("Postgres");
    var connectionString = pgSection.GetValue<string>("ConnectionString");
    if (string.IsNullOrEmpty(connectionString))
    {
        // Build from individual settings (managed or external)
        var pgPort = pgSection.GetValue<int?>("Port") ?? 5433;
        var pgDb = pgSection.GetValue<string>("Database") ?? "cove";
        connectionString = $"Host=127.0.0.1;Port={pgPort};Database={pgDb};Username=postgres;Trust Server Certificate=true;Minimum Pool Size=2;Maximum Pool Size=100;Timeout=15;Command Timeout=30";
    }
    coveCfgInstance.DatabaseConnectionString = connectionString;
    builder.Services.AddCoveData(connectionString);

    // Event bus (singleton for cross-service communication)
    builder.Services.AddSingleton<IEventBus, EventBus>();

    // Job service (background task processing)
    builder.Services.AddSingleton<JobService>();
    builder.Services.AddSingleton<IJobService>(sp => sp.GetRequiredService<JobService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<JobService>());

    // Gallery services (zip reading, gallery parsing, etc.)
    builder.Services.AddGalleryServices();

    // Application services
    builder.Services.AddSingleton<IThumbnailService, ThumbnailService>();
    builder.Services.AddSingleton<IFingerprintService, FingerprintService>();
    builder.Services.AddSingleton<IFaceSuggester, EmptyFaceSuggester>();
    builder.Services.AddScoped<IScanService, ScanService>();
    builder.Services.AddScoped<IStreamService, StreamService>();
    builder.Services.AddScoped<ICleanService, CleanService>();
    builder.Services.AddSingleton<Cove.Api.Services.MaintenanceState>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddSingleton<IBlobService, BlobService>();
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<IFfmpegCapabilities, FfmpegCapabilitiesService>();
    builder.Services.AddSingleton<AuthBypassPrincipalProvider>();
    builder.Services.AddSingleton<ScraperService>();
    builder.Services.AddSingleton<IVideoCoverService, VideoCoverService>();
    builder.Services.AddScoped<IVideoMetadataApplyService, VideoMetadataApplyService>();
    builder.Services.AddScoped<IGroupMetadataApplyService, GroupMetadataApplyService>();
    builder.Services.AddScoped<PerformerScrapeService>();
    builder.Services.AddScoped<ScrapeAttemptService>();
    builder.Services.AddSingleton<DownloaderService>();
    builder.Services.AddSingleton<ITranscodeService, TranscodeService>();
    builder.Services.AddScoped<StashMigrationService>();
    builder.Services.AddScoped<ITagProvenanceService, TagProvenanceService>();
    builder.Services.AddScoped<IFieldProvenanceService, FieldProvenanceService>();
    builder.Services.AddScoped<TagApplicationService>();
    builder.Services.AddScoped<CustomFieldService>();
    builder.Services.AddSingleton<TextExtractionService>();
    builder.Services.AddScoped<AiDataPurgeService>();
    builder.Services.AddScoped<DynamicGroupResolver>();
    builder.Services.AddScoped<IDynamicGroupSource, FilterDynamicGroupSource>();
    builder.Services.AddScoped<IDynamicGroupSource, SaveForLaterDynamicGroupSource>();
    builder.Services.AddScoped<IDynamicGroupSource, WatchHistoryDynamicGroupSource>();
    builder.Services.AddScoped<IDynamicGroupSource, ContinueWatchingDynamicGroupSource>();
    builder.Services.AddHttpClient("scraper", client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(45);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer(),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
    builder.Services.AddHttpClient<MetadataServerService>();
    // Runtime extensions can bind only Cove.Core types, so surface the Cove.Api metadata-server client
    // through its Cove.Core interface (as IReferencePerformerImporter below does).
    builder.Services.AddTransient<IMetadataServerService>(sp => sp.GetRequiredService<MetadataServerService>());
    // Lets extensions (AI.Faces) enrich a newly-created performer from a configured metadata server
    // when a reference/SAIE match is accepted. Singleton so it is shared into extension containers; it
    // opens its own scope per call.
    builder.Services.AddSingleton<IReferencePerformerImporter, ReferencePerformerImporter>();

    // Extension system
    var extensionsDataDir = CoveDefaultPaths.GetDataSubdirectory("extensions");
    Directory.CreateDirectory(extensionsDataDir);
    var coveVersion = Cove.Core.Common.CoveVersion.Display;
    var extensionContext = new ExtensionContext
    {
        Configuration = builder.Configuration,
        DataDirectory = extensionsDataDir,
        CoveVersion = coveVersion
    };
    var extensionManager = new ExtensionManager(extensionContext);
    // Discover .NET plugin DLLs from extensions directory
    extensionManager.DiscoverExtensions(extensionsDataDir);
    // Register built-in extensions
    extensionManager.Register(new Cove.Api.Extensions.ThemeCollectionExtension());
    extensionManager.Register(new Cove.Api.Extensions.DirectFileDownloaderExtension());
    CoveContext.SetDataExtensions(extensionManager.Extensions.OfType<IDataExtension>());
    // Keep the EF model's data-extension set in sync when extensions are installed/uninstalled at
    // runtime, so a newly installed data extension's entity types resolve without an app restart.
    extensionManager.DataExtensionsChanged = () =>
        CoveContext.SetDataExtensions(extensionManager.Extensions.OfType<IDataExtension>());
    builder.Services.AddSingleton(extensionManager);
    builder.Services.AddSingleton<IExtensionContributionRuntime>(extensionManager);
    builder.Services.AddSingleton<IExtensionEntityFilterRuntime, ExtensionEntityFilterRuntime>();
    builder.Services.AddScoped<ExtensionEntityFilterService>();
    // The cross-extension capability/service exchange: extensions publish shared-contract services
    // here and consume siblings' contributions, since each extension lives in its own container.
    builder.Services.AddSingleton<IExtensionServiceExchange, ExtensionServiceExchange>();
    builder.Services.AddSingleton<IExtensionStoreFactory>(sp => new Cove.Data.Repositories.EfExtensionStoreFactory(sp));
    builder.Services.AddSingleton<IExtensionRegistry>(sp =>
    {
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var http = httpFactory.CreateClient("ExtensionRegistry");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cove/1.0");
        return new GitHubExtensionRegistry(http, coveVersion: extensionContext.CoveVersion);
    });
    builder.Services.AddHttpClient("ExtensionRegistry");
    builder.Services.AddHostedService<ExtensionEventBridge>();
    extensionManager.ConfigureServices(builder.Services);

    // Managed PostgreSQL — auto-downloads and runs a local PG instance
    var pgManaged = pgSection.GetValue<bool?>("Managed") ?? true;
    if (pgManaged)
        builder.Services.AddHostedService<PostgresManagerService>();

    // Auth bootstrap (must run AFTER PostgresManagerService so the DB is reachable).
    builder.Services.AddSingleton<Cove.Data.Auth.BootstrapAuthService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Cove.Data.Auth.BootstrapAuthService>());

    // FFmpeg — auto-downloads if not found in PATH or configured path
    builder.Services.AddHostedService<FfmpegManagerService>();

    // SignalR
    builder.Services.AddSignalR()
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

    // Auth
    var authConfig = coveConfig.GetSection("Auth");
    var jwtSecret = coveCfgInstance.Auth.JwtSecret;
    var authEnabled = authConfig.GetValue<bool>("Enabled");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = "Cove",
                ValidAudience = "Cove",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
            };
            // Allow SignalR to authenticate via query string
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        context.Token = accessToken;
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();

    // MVC + OpenAPI
    builder.Services.AddControllers(options =>
    {
        options.Filters.Add<Cove.Api.Middleware.EntityEventFilter>();
        options.Filters.Add<Cove.Api.Middleware.AuthExceptionFilter>();
        options.Filters.Add<Cove.Api.Middleware.PermissionAuthorizationFilter>();
        options.Filters.Add<Cove.Api.Middleware.EntityAccessActionFilter>();
    })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
    builder.Services.AddOpenApi();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Response compression â€” reduces 22KB video lists to ~2KB
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

    // Output caching for read-heavy API endpoints
    builder.Services.AddOutputCache(options =>
    {
        options.AddBasePolicy(b => b.NoCache());
        options.AddPolicy("ShortCache", b => b
            .AddPolicy<Cove.Api.Middleware.AuthAwareOutputCachePolicy>()
            .Expire(TimeSpan.FromSeconds(1))
            .SetVaryByHeader("Authorization", "Cookie", "X-Share-Token", "X-Share-Password")
            .SetLocking(false), true);
    });

    // In-memory cache for POST query results
    builder.Services.AddMemoryCache();

    // Rate limiting — tight bucket on auth endpoints to slow brute-force; lenient global default.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Tight: /api/auth/login + /api/auth/refresh — 10 requests / 15s sliding window per IP+username.
        options.AddPolicy("auth-strict", httpContext =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var key = ip;
            // Best-effort: combine with submitted username so two users behind a NAT don't lock each other.
            if (httpContext.Request.HasJsonContentType() && httpContext.Request.ContentLength is > 0 and < 4096)
            {
                // body cannot be read here without buffering; key on IP only.
            }
            return System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
                new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromSeconds(15),
                    SegmentsPerWindow = 3,
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                });
        });

        options.AddPolicy("interactions", httpContext =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var authHeader = httpContext.Request.Headers.Authorization.ToString();
            var key = !string.IsNullOrWhiteSpace(authHeader) ? authHeader : ip;

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 240,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
    });

    // CORS - allow frontend dev server
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    // Snapshot the fully-assembled host services so the extension overlay can share the host's
    // singletons and re-create its scoped services. Must happen after every core/built-in
    // registration and before the root container is built (which makes it immutable).
    extensionManager.CaptureHostServices(builder.Services);

    var app = builder.Build();

    // Middleware pipeline
    // UseSerilogRequestLogging removed â€” adds 3-5ms per request overhead
    app.UseMiddleware<Cove.Api.Middleware.OperationLogContextMiddleware>();
    app.UseMiddleware<Cove.Api.Middleware.CompoundSortValidationMiddleware>();
    app.UseMiddleware<Cove.Api.Middleware.DatabaseUnavailableMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseResponseCompression();
    app.UseCors();
    app.UseOutputCache();
    app.UseRateLimiter();
    app.UseMiddleware<Cove.Api.Middleware.OutsideIpFailsafeMiddleware>();

    // Extension middleware (runs before auth, after CORS). A single persistent dispatcher invokes the
    // live chain of enabled IMiddlewareExtension instances, so middleware from a runtime-installed
    // extension takes effect immediately without a host restart.
    app.Use((context, next) => extensionManager.InvokeMiddlewareChainAsync(context, _ => next()));

    if (authEnabled)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    // Always run our principal-resolver middleware so [RequiresPermission] can read it.
    // (When auth is disabled the filter short-circuits and treats requests as anonymous-allowed.)
    app.UseMiddleware<Cove.Api.Middleware.CurrentPrincipalMiddleware>();

    // Authorize extension endpoints with the host request scope before exposing extension-owned
    // registrations to endpoint execution. This prevents an extension from replacing Cove's
    // principal, authorization, or audit services for its own authorization decision.
    Cove.Api.Middleware.ExtensionEndpointPipeline.Configure(
        app,
        extensionManager.CreateExtensionScope);

    app.MapGet("/health", async (CoveContext db, CancellationToken ct) =>
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? Results.Ok(new { status = "ok" })
                : Results.Problem("Database connectivity check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).AllowAnonymous();

    app.MapControllers();
    app.MapHub<JobHub>("/hubs/jobs");
    app.MapHub<LogHub>("/hubs/logs");
    extensionManager.SetRouteBuilder(app);
    // Build the extension overlay before mapping dynamic endpoints so extension handler parameters
    // are correctly recognized as DI services at endpoint-build time.
    extensionManager.PrepareRuntimeServices(app.Services);
    extensionManager.SetupDynamicEndpoints();

    if (!isTestHarness)
    {
        // Serve SPA static files (production)
        // When running as a single-file executable, wwwroot is embedded as managed resources.
        // Fall back to the embedded file provider when the physical wwwroot folder is absent.
        var webRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        IFileProvider? spaFileProvider = null;
        if (Directory.Exists(webRootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        else
        {
            spaFileProvider = new ManifestEmbeddedFileProvider(
                typeof(Program).Assembly, "wwwroot");
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFileProvider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFileProvider });
        }

        if (spaFileProvider != null)
        {
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFileProvider });
        }
        else
        {
            app.MapFallbackToFile("index.html");
        }
    }

    var port = coveConfig.GetValue<int?>("Port") ?? 5073;
    if (!isTestHarness)
        app.Urls.Add($"http://0.0.0.0:{port}");

    // Initialize SignalR log sink with hub context
    SignalRLogSink.SetHubContext(app.Services.GetRequiredService<IHubContext<LogHub>>());

    // FfmpegInProcess is a static class with no injected logger — give it one for init diagnostics.
    FfmpegInProcess.Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cove.Api.Services.FfmpegInProcess");

    if (isIntegrationTest)
    {
        app.Run();
        return;
    }

    // Kestrel starts accepting requests here, but on first boot the database is still empty until the
    // baseline migration below runs. Flag the schema as initializing so DatabaseUnavailableMiddleware
    // returns a clean 503 to any request arriving in that window instead of a 500 ("relation ... does
    // not exist"). Released as soon as the schema is confirmed ready (below).
    var schemaInitToken = app.Services.GetRequiredService<Cove.Api.Services.MaintenanceState>().BeginInitialization();
    await app.StartAsync();

    if (!isIntegrationTest)
    {
        if (!isTestHarness)
        {
            // Load saved user config (cove-config.json) and apply on top of appsettings.json
            var configSvc = app.Services.GetRequiredService<ConfigService>();
            var savedConfig = await configSvc.LoadSavedConfigAsync();
            if (savedConfig != null)
            {
                await configSvc.SaveConfigAsync(savedConfig); // applies to live IOptions
                Log.Information("Loaded user configuration from {Path}", configSvc.ConfigPath);
            }
        }

        // Prepare database + pre-warm EF Core and connection pool
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var schemaCurrent = true;

            var isPostgresProvider = string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal);

            if (isPostgresProvider)
            {
                await EnsurePostgresMigrationInfrastructureAsync(db);

                if (!await HasPostgresApplicationTablesAsync(db))
                {
                    Log.Information("Empty database detected - applying V1.0 baseline migration");
                    await db.Database.MigrateAsync();
                    Log.Information("Database created via V1.0 baseline migration");
                }
                else
                {
                    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToArray();
                    if (pendingMigrations.Length > 0)
                    {
                        schemaCurrent = false;
                        Log.Warning(
                            "Database has {Count} pending migration(s): {Migrations}.",
                            pendingMigrations.Length,
                            string.Join(", ", pendingMigrations));
                    }
                    else
                    {
                        if (!await HasPostgresTableAsync(db, "videos"))
                        {
                            schemaCurrent = false;
                            Log.Warning(
                                "Database is missing required table public.videos. Startup will skip model-dependent warmup until migrations are applied.");
                        }
                        else if (!await HasPostgresColumnAsync(db, "groups", "ShowInVideoLists"))
                        {
                            schemaCurrent = false;
                            Log.Warning(
                                "Database is missing required column public.groups.\"ShowInVideoLists\". Startup will skip model-dependent warmup until migrations are applied.");
                        }
                        else
                        {
                            Log.Information("Database is up to date");
                        }
                    }
                }
            }
            else
            {
                await db.Database.EnsureCreatedAsync();
            }

            // Schema is now present (freshly migrated or already current); allow DB-backed requests through.
            schemaInitToken.Dispose();

            if (schemaCurrent)
            {
                await scope.ServiceProvider
                    .GetRequiredService<DynamicGroupResolver>()
                    .EnsureBuiltInGroupsAsync(CancellationToken.None);

                // Pre-warm: compile EF Core query cache, prime connection pool, JIT hot paths
                _ = await db.Videos.CountAsync();
                _ = await db.Videos.AsNoTracking()
                    .OrderBy(s => s.Id)
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .Include(s => s.VideoTags).ThenInclude(st => st.Tag)
                    .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
                    .Take(1).AsSplitQuery().ToListAsync();
                Log.Information("EF Core and connection pool pre-warmed");
            }
            else
            {
                Log.Warning("Skipping built-in group sync because database migrations are pending manual approval");
                Log.Warning("Skipping EF Core pre-warm because database migrations are pending manual approval");
            }
        }

        // Initialize extensions after database is ready
        await extensionManager.InitializeAllAsync(app.Services);

        // Collect extension-contributed permissions and content policies (auth integration).
        {
            var permissionRegistry = app.Services.GetRequiredService<Cove.Core.Auth.IPermissionRegistry>();
            foreach (var ext in extensionManager.Extensions)
            {
                if (ext is Cove.Sdk.IPermissionContributor pc)
                {
                    try
                    {
                        var contributed = pc.ContributePermissions().ToList();
                        var rejected = permissionRegistry.RegisterExtensionPermissions(ext.Id, contributed);
                        foreach (var rejection in rejected)
                        {
                            Log.Warning(
                                "Extension {Id} permission {PermissionKey} rejected: {Reason}",
                                ext.Id,
                                rejection.PermissionKey,
                                rejection.Reason);
                        }
                        Log.Information(
                            "Extension {Id} contributed {AcceptedCount} permission(s); {RejectedCount} rejected",
                            ext.Id,
                            contributed.Count - rejected.Count,
                            rejected.Count);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Extension {Id} failed to contribute permissions", ext.Id);
                    }
                }
                if (ext is Cove.Sdk.IContentPolicyContributor cp)
                {
                    try
                    {
                        var policies = cp.ContributePolicies();
                        Log.Information("Extension {Id} contributed {Count} content policy/policies", ext.Id, policies.Count);
                        // Policies are recorded for audit/inspection. Full enforcement at the EF
                        // query-filter layer is part of Schema C Stage 2; v1 stores them so the
                        // surface is documented and the auth services can consult them.
                        Cove.Core.Auth.ContentPolicyRegistry.Register(ext.Id, policies);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Extension {Id} failed to contribute content policies", ext.Id);
                    }
                }
            }

            await app.Services.GetRequiredService<Cove.Data.Auth.BootstrapAuthService>()
                .RefreshPermissionCatalogAsync(CancellationToken.None);
        }

    }

    Log.Information(
        "Cove starting on port {Port} — dataRoot={DataRoot}, managedPostgres={ManagedPostgres}, authEnabled={AuthEnabled}, logLevel={LogLevel}",
        port,
        CoveDefaultPaths.GetDataRoot(),
        coveCfgInstance.Postgres.Managed,
        coveCfgInstance.Auth.Enabled,
        runtimeLogLevelManager.LevelSwitch.MinimumLevel);
    await app.WaitForShutdownAsync();

    // Graceful shutdown for extensions
    await extensionManager.ShutdownAllAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    // Log.Logger may still be the silent bootstrap default if the crash happened
    // before the logging pipeline was built, so also write to stderr to guarantee
    // some diagnostic output for very-early startup failures.
    Console.Error.WriteLine($"FATAL: Application terminated unexpectedly: {ex}");

    if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "IntegrationTest", StringComparison.Ordinal))
        throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program
{
}
