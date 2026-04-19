using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Core.Entities.Galleries;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;

// Ensure enough threads for async I/O under concurrent load
ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 4);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.Sink(new SignalRLogSink()));

    // Bind configuration
    var coveConfig = builder.Configuration.GetSection("Cove");
    builder.Services.Configure<CoveConfiguration>(coveConfig);
    builder.Services.Configure<AuthConfig>(coveConfig.GetSection("Auth"));
    builder.Services.Configure<PostgresConfig>(coveConfig.GetSection("Postgres"));

    // Register a singleton CoveConfiguration instance so all consumers share the same mutable object
    var coveCfgInstance = coveConfig.Get<CoveConfiguration>() ?? new CoveConfiguration();
    builder.Services.AddSingleton(coveCfgInstance);

    // Database - EF Core + PostgreSQL
    var pgSection = coveConfig.GetSection("Postgres");
    var connectionString = pgSection.GetValue<string>("ConnectionString");
    if (string.IsNullOrEmpty(connectionString))
    {
        // Build from individual settings (managed or external)
        var pgPort = pgSection.GetValue<int?>("Port") ?? 5433;
        var pgDb = pgSection.GetValue<string>("Database") ?? "cove";
        connectionString = $"Host=127.0.0.1;Port={pgPort};Database={pgDb};Username=postgres;Trust Server Certificate=true;Minimum Pool Size=10;Maximum Pool Size=200;Timeout=15;Command Timeout=30";
    }
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
    builder.Services.AddScoped<IScanService, ScanService>();
    builder.Services.AddScoped<IStreamService, StreamService>();
    builder.Services.AddScoped<IAutoTagService, AutoTagService>();
    builder.Services.AddScoped<ICleanService, CleanService>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddSingleton<IBlobService, BlobService>();
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<ScraperService>();
    builder.Services.AddSingleton<ITranscodeService, TranscodeService>();
    builder.Services.AddHttpClient("scraper");
    builder.Services.AddHttpClient<MetadataServerService>();

    // Extension system
    var extensionsDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cove", "extensions");
    Directory.CreateDirectory(extensionsDataDir);
    var extensionContext = new ExtensionContext
    {
        Configuration = builder.Configuration,
        DataDirectory = extensionsDataDir,
        CoveVersion = "1.0.0"
    };
    var extensionManager = new ExtensionManager(extensionContext);
    // Discover .NET plugin DLLs from extensions directory
    extensionManager.DiscoverExtensions(extensionsDataDir);
    // Register built-in extensions
    extensionManager.Register(new Cove.Api.Extensions.ThemeCollectionExtension());
    extensionManager.Register(new Cove.Api.Extensions.SceneAnalyticsExtension());
    extensionManager.Register(new Cove.Api.Extensions.CustomHomeExtension());
    extensionManager.Register(new Cove.Api.Extensions.SystemToolsExtension());
    extensionManager.Register(new Cove.Api.Extensions.NotificationSettingsExtension());
    extensionManager.Register(new Cove.Api.Extensions.EnhancedDeleteDialogExtension());
    extensionManager.Register(new Cove.Api.Extensions.AuditLogExtension());
    builder.Services.AddSingleton(extensionManager);
    builder.Services.AddSingleton<IExtensionStoreFactory>(sp => new Cove.Data.Repositories.EfExtensionStoreFactory(sp));
    builder.Services.AddSingleton<IExtensionRegistry>(sp =>
    {
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var http = httpFactory.CreateClient("ExtensionRegistry");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cove/1.0");
        return new GitHubExtensionRegistry(http);
    });
    builder.Services.AddHttpClient("ExtensionRegistry");
    extensionManager.ConfigureServices(builder.Services);

    // Managed PostgreSQL — auto-downloads and runs a local PG instance
    var pgManaged = pgSection.GetValue<bool?>("Managed") ?? true;
    if (pgManaged)
        builder.Services.AddHostedService<PostgresManagerService>();

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
    var jwtSecret = authConfig.GetValue<string>("JwtSecret") ?? Guid.NewGuid().ToString();
    var authEnabled = authConfig.GetValue<bool>("Enabled");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
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
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
    builder.Services.AddOpenApi();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Response compression â€” reduces 22KB scene lists to ~2KB
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
        options.AddPolicy("ShortCache", b => b.Expire(TimeSpan.FromSeconds(1)).SetVaryByQuery("*").SetLocking(false));
    });

    // In-memory cache for POST query results
    builder.Services.AddMemoryCache();

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

    var app = builder.Build();

    // Middleware pipeline
    // UseSerilogRequestLogging removed â€” adds 3-5ms per request overhead

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseResponseCompression();
    app.UseCors();
    app.UseOutputCache();

    // Extension middleware (runs before auth, after CORS)
    extensionManager.ConfigureMiddleware(app);

    if (authEnabled)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    app.MapControllers();
    app.MapHub<JobHub>("/hubs/jobs");
    app.MapHub<LogHub>("/hubs/logs");
    extensionManager.MapEndpoints(app);

    // Serve SPA static files (production)
    // When running as a single-file executable, wwwroot is embedded as managed resources.
    // Fall back to the embedded file provider when the physical wwwroot folder is absent.
    var webRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(webRootPath))
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
    }
    else
    {
        var embeddedProvider = new ManifestEmbeddedFileProvider(
            typeof(Program).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });
    }
    app.MapFallbackToFile("index.html");

    var port = coveConfig.GetValue<int?>("Port") ?? 9999;
    app.Urls.Add($"http://0.0.0.0:{port}");

    // Initialize SignalR log sink with hub context
    SignalRLogSink.SetHubContext(app.Services.GetRequiredService<IHubContext<LogHub>>());

    // Start hosted services (including managed PostgreSQL) before database creation.
    await app.StartAsync();

    // Load saved user config (cove-config.json) and apply on top of appsettings.json
    var configSvc = app.Services.GetRequiredService<ConfigService>();
    var savedConfig = await configSvc.LoadSavedConfigAsync();
    if (savedConfig != null)
    {
        await configSvc.SaveConfigAsync(savedConfig); // applies to live IOptions
        Log.Information("Loaded user configuration from {Path}", configSvc.ConfigPath);
    }

    // Auto-migrate database + pre-warm EF Core and connection pool
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        // Determine if this is a brand-new database or an existing one that predates migrations.
        var canConnect = await db.Database.CanConnectAsync();
        var hasMigrationHistory = false;
        if (canConnect)
        {
            // Check if __EFMigrationsHistory table exists (indicates migrations-aware DB)
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='__EFMigrationsHistory'";
            hasMigrationHistory = await cmd.ExecuteScalarAsync() != null;
            await conn.CloseAsync();
        }

        var hasTables = false;
        if (canConnect && !hasMigrationHistory)
        {
            // Check if core tables exist (pre-migration database)
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='scenes'";
            hasTables = await cmd.ExecuteScalarAsync() != null;
            await conn.CloseAsync();
        }

        if (hasTables && !hasMigrationHistory)
        {
            // Existing database created with EnsureCreatedAsync — baseline it.
            // Mark the initial migration as already applied so MigrateAsync only runs future migrations.
            Log.Information("Existing database detected — baselining migration history");
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            await using var createHistory = conn.CreateCommand();
            createHistory.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                )
            """;
            await createHistory.ExecuteNonQueryAsync();

            await using var insertBaseline = conn.CreateCommand();
            insertBaseline.CommandText = """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260419000753_InitialCreate', '10.0.5')
                ON CONFLICT DO NOTHING
            """;
            await insertBaseline.ExecuteNonQueryAsync();
            await conn.CloseAsync();

            // Still run compatibility patches for pre-migration databases
            await EnsureColumnsAsync(db);

            Log.Information("Migration history baselined — future migrations will apply automatically");
        }

        // Check for pending migrations
        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToArray();

        if (pendingMigrations.Length > 0)
        {
            Log.Information("Applying {Count} pending migration(s): {Migrations}",
                pendingMigrations.Length, string.Join(", ", pendingMigrations));

            // Automatic backup before migration
            try
            {
                var backupSvc = scope.ServiceProvider.GetRequiredService<IBackupService>();
                var backupJobId = backupSvc.StartBackup();
                Log.Information("Pre-migration backup started (job {JobId})", backupJobId);
                // Give the backup a moment to begin (it runs as a background job)
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pre-migration backup failed — proceeding with migration anyway. " +
                    "Manual backup recommended.");
            }

            await db.Database.MigrateAsync();
            Log.Information("Database migrations applied successfully");
        }
        else if (!hasTables && !hasMigrationHistory)
        {
            // Brand new database — apply all migrations from scratch
            Log.Information("New database detected — applying all migrations");
            await db.Database.MigrateAsync();
            Log.Information("Database created via migrations");
        }
        else
        {
            Log.Information("Database is up to date");
        }

        // Fix oshash values: Go uses %016x (zero-padded 16 chars), ensure all values match
        await NormalizeOshashValuesAsync(db);

        // Pre-warm: compile EF Core query cache, prime connection pool, JIT hot paths
        _ = await db.Scenes.CountAsync();
        _ = await db.Scenes.AsNoTracking()
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.SceneTags).ThenInclude(st => st.Tag)
            .Include(s => s.ScenePerformers).ThenInclude(sp => sp.Performer)
            .Take(1).AsSingleQuery().ToListAsync();
        Log.Information("EF Core and connection pool pre-warmed");
    }

    // Initialize extensions after database is ready
    await extensionManager.InitializeAllAsync(app.Services);

    Log.Information("Cove starting on port {Port}", port);
    await app.WaitForShutdownAsync();

    // Graceful shutdown for extensions
    await extensionManager.ShutdownAllAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task EnsureColumnsAsync(CoveContext db)
{
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();

    async Task AddColumnIfMissing(string table, string column, string type, string? defaultValue = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT column_name FROM information_schema.columns WHERE table_name='{table}' AND column_name='{column}'";
        var exists = await cmd.ExecuteScalarAsync();
        if (exists != null) return;

        var def = defaultValue != null ? $" DEFAULT {defaultValue}" : "";
        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}{def}";
        await alter.ExecuteNonQueryAsync();
    }

    async Task EnsureTableExists(string table, string createSql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='{table}'";
        var exists = await cmd.ExecuteScalarAsync();
        if (exists != null) return;

        await using var create = conn.CreateCommand();
        create.CommandText = createSql;
        await create.ExecuteNonQueryAsync();
    }

    // Gallery cover image support
    await AddColumnIfMissing("galleries", "ImageBlobId", "text");
    await AddColumnIfMissing("galleries", "CoverImageId", "integer");

    // Remote ID support added after the initial database schema.
    await EnsureTableExists("SceneRemoteId", """
        CREATE TABLE IF NOT EXISTS "SceneRemoteId" (
            "Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "SceneId" integer NOT NULL REFERENCES "scenes"("Id") ON DELETE CASCADE,
            "Endpoint" text NOT NULL,
            "RemoteId" text NOT NULL
        )
    """);
    await EnsureTableExists("PerformerRemoteId", """
        CREATE TABLE IF NOT EXISTS "PerformerRemoteId" (
            "Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "PerformerId" integer NOT NULL REFERENCES "performers"("Id") ON DELETE CASCADE,
            "Endpoint" text NOT NULL,
            "RemoteId" text NOT NULL
        )
    """);
    await EnsureTableExists("TagRemoteId", """
        CREATE TABLE IF NOT EXISTS "TagRemoteId" (
            "Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "TagId" integer NOT NULL REFERENCES "tags"("Id") ON DELETE CASCADE,
            "Endpoint" text NOT NULL,
            "RemoteId" text NOT NULL
        )
    """);
    await EnsureTableExists("StudioRemoteId", """
        CREATE TABLE IF NOT EXISTS "StudioRemoteId" (
            "Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "StudioId" integer NOT NULL REFERENCES "studios"("Id") ON DELETE CASCADE,
            "Endpoint" text NOT NULL,
            "RemoteId" text NOT NULL
        )
    """);

    var remoteIdIndexCommands = new[]
    {
        "CREATE INDEX IF NOT EXISTS \"IX_SceneRemoteId_SceneId\" ON \"SceneRemoteId\" (\"SceneId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_PerformerRemoteId_PerformerId\" ON \"PerformerRemoteId\" (\"PerformerId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_TagRemoteId_TagId\" ON \"TagRemoteId\" (\"TagId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_StudioRemoteId_StudioId\" ON \"StudioRemoteId\" (\"StudioId\")",
    };

    foreach (var sql in remoteIdIndexCommands)
    {
        await using var indexCommand = conn.CreateCommand();
        indexCommand.CommandText = sql;
        await indexCommand.ExecuteNonQueryAsync();
    }

    // Extension key-value storage (added after initial schema)
    await EnsureTableExists("extension_data", """
        CREATE TABLE IF NOT EXISTS "extension_data" (
            "ExtensionId" text NOT NULL,
            "Key" text NOT NULL,
            "Value" text NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            PRIMARY KEY ("ExtensionId", "Key")
        )
    """);

    await using (var extIndex = conn.CreateCommand())
    {
        extIndex.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_extension_data_ExtensionId\" ON \"extension_data\" (\"ExtensionId\")";
        await extIndex.ExecuteNonQueryAsync();
    }

    // Video captions support
    // Create the table if it does not exist before checking for missing columns.
    await EnsureTableExists("VideoCaptions", """
        CREATE TABLE IF NOT EXISTS "VideoCaptions" (
            "Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "FileId" integer NOT NULL REFERENCES "files"("Id") ON DELETE CASCADE,
            "LanguageCode" text NOT NULL DEFAULT '00',
            "CaptionType" text NOT NULL DEFAULT 'vtt',
            "Filename" text NOT NULL
        )
    """);

    await AddColumnIfMissing("VideoCaptions", "Id", "integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY");

    // FK indexes on the files table for faster lookups (e.g. streaming by SceneId)
    var fileIndexCommands = new[]
    {
        """CREATE INDEX IF NOT EXISTS "IX_files_SceneId" ON "files" ("SceneId")""",
        """CREATE INDEX IF NOT EXISTS "IX_files_ImageId" ON "files" ("ImageId")""",
    };
    foreach (var sql in fileIndexCommands)
    {
        await using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText = sql;
        await indexCmd.ExecuteNonQueryAsync();
    }

    await conn.CloseAsync();
}

/// <summary>
/// Normalize oshash fingerprint values to match Go's fmt.Sprintf("%016x") format:
/// zero-padded to exactly 16 hex characters. Previously the C# implementation
/// used unpadded hex which caused metadata-server matching failures.
/// </summary>
static async Task NormalizeOshashValuesAsync(CoveContext db)
{
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();

    // Fix oshash values â€” SQLite uses substr/replace for zero-padding
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE "FileFingerprints"
        SET "Value" = substr('0000000000000000' || "Value", -16, 16)
        WHERE "Type" = 'oshash' AND length("Value") < 16
    """;
    var affected = await cmd.ExecuteNonQueryAsync();

    // Ensure join table indexes exist for filter performance (EF migrations may not create them for existing DBs)
    var indexCommands = new[]
    {
        "CREATE INDEX IF NOT EXISTS \"IX_scene_tags_TagId\" ON \"scene_tags\" (\"TagId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_scene_performers_PerformerId\" ON \"scene_performers\" (\"PerformerId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_image_tags_TagId\" ON \"image_tags\" (\"TagId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_image_performers_PerformerId\" ON \"image_performers\" (\"PerformerId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_image_galleries_GalleryId\" ON \"image_galleries\" (\"GalleryId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_gallery_tags_TagId\" ON \"gallery_tags\" (\"TagId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_gallery_performers_PerformerId\" ON \"gallery_performers\" (\"PerformerId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_performer_tags_TagId\" ON \"performer_tags\" (\"TagId\")",
        "CREATE INDEX IF NOT EXISTS \"IX_FileFingerprints_Type_Value\" ON \"FileFingerprints\" (\"Type\", \"Value\")",
    };

    foreach (var sql in indexCommands)
    {
        try
        {
            await using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = sql;
            await idxCmd.ExecuteNonQueryAsync();
        }
        catch { /* index may already exist */ }
    }

    await conn.CloseAsync();

    if (affected > 0)
        Log.Information("Normalized {Count} oshash fingerprint values to 16-char padded format", affected);
}
