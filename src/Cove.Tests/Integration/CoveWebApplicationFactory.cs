using System.Net.Http.Headers;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Cove.Tests.Integration;

/// <summary>
/// Hosts the real API on its own <see cref="CovePostgresDatabase"/>, created before the server first
/// starts and dropped when the factory is disposed. Tests that start the server need a PostgreSQL
/// server, so mark them <see cref="RequiresPostgresAttribute"/>.
/// </summary>
public sealed class CoveWebApplicationFactory : WebApplicationFactory<Program>
{
    public const int TestUserId = 1;
    public const int TestExternalUserId = 2;

    private static readonly object ServerStartEnvironmentLock = new();

    private readonly string _environmentName;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly object _databaseLock = new();
    private Task<CovePostgresDatabase>? _database;
    private bool _databaseDisposed;
    private bool _serverStarted;

    public CoveWebApplicationFactory(
        string environmentName = "IntegrationTest",
        Action<IServiceCollection>? configureTestServices = null)
    {
        _environmentName = environmentName;
        _configureTestServices = configureTestServices;
        UseKestrel(0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The host is built synchronously, so a server started by CreateClient or Services waits here for
        // the database. ResetDatabaseAsync awaits the same task before it starts the server.
        var database = EnsureDatabaseAsync().GetAwaiter().GetResult();

        builder.UseEnvironment(_environmentName);
        // Program reads Cove:Postgres while it composes the services, before the host is built and before
        // ConfigureAppConfiguration sources apply, so these go in as host settings, which also take
        // precedence over COVE__ environment variables. The connection string becomes
        // CoveConfiguration.DatabaseConnectionString, which services that open their own connections use,
        // such as the custom-field index reconciler, backups and the database controller.
        builder.UseSetting("Cove:Postgres:Managed", "false");
        builder.UseSetting("Cove:Postgres:ConnectionString", database.ConnectionString);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cove:Auth:Enabled"] = "true",
                ["Cove:Auth:JwtSecret"] = "integration-test-secret",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITokenService>();
            services.RemoveAll<IExistingUserPrincipalResolver>();
            services.RemoveAll<NpgsqlDataSource>();

            services.AddScoped<ITokenService, IntegrationTestTokenService>();
            services.AddScoped<IExistingUserPrincipalResolver>(provider =>
                (IExistingUserPrincipalResolver)provider.GetRequiredService<ITokenService>());
            // Production's CoveContext registration takes its data source from the container, so the
            // contexts keep production's options, interceptors and retry strategy on this database.
            // Registered as an instance, so the container leaves it to the database to dispose.
            services.AddSingleton(database.DataSource);
            _configureTestServices?.Invoke(services);
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        EnsureServerStarted();

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "integration-test-token");
        return client;
    }

    /// <summary>
    /// Empties every application table of the migrated database and seeds the test user. The schema and
    /// the migration history stay, so the database is still the one production's migrations built.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        var database = await EnsureDatabaseAsync();
        EnsureServerStarted();

        // The auth bootstrap seeds permissions and roles in the background as soon as the schema exists.
        // Stop it first so it cannot write between the truncate and the seed.
        await Services.GetRequiredService<BootstrapAuthService>().StopAsync(CancellationToken.None);

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.ExecuteSqlRawAsync("""
            DO $reset$
            DECLARE
                table_list text;
            BEGIN
                SELECT string_agg(format('%I.%I', schemaname, tablename), ', ')
                INTO table_list
                FROM pg_tables
                WHERE schemaname = 'public'
                  AND tablename <> '__EFMigrationsHistory';

                IF table_list IS NOT NULL THEN
                    EXECUTE 'TRUNCATE TABLE ' || table_list || ' RESTART IDENTITY CASCADE';
                END IF;
            END
            $reset$;
            """);

        db.Users.Add(new User
        {
            Id = TestUserId,
            Username = "integration-user",
            PasswordHash = "integration-test",
            PasswordAlgo = "integration-test",
            IsActive = true,
            IsSystem = true,
        });
        db.ExternalIdentityLinks.Add(new ExternalIdentityLink
        {
            UserId = TestUserId,
            ExtensionId = "integration.login",
            ProviderId = "integration-provider",
            Subject = "integration-subject",
            ProviderLabel = "Integration provider",
            AccountLabel = "integration-user",
        });
        await db.SaveChangesAsync();
        await database.SyncIdentitySequencesAsync();
    }

    /// <summary>
    /// Moves the identity sequences past rows a test saved with explicit ids, so a later row saved without
    /// an id does not collide with them.
    /// </summary>
    public async Task SyncIdentitySequencesAsync()
    {
        var database = await EnsureDatabaseAsync();
        await database.SyncIdentitySequencesAsync();
    }

    /// <summary>The connection string of the database this factory hosts the API on.</summary>
    public async Task<string> GetDatabaseConnectionStringAsync()
        => (await EnsureDatabaseAsync()).ConnectionString;

    public async Task WithDbContextAsync(Func<CoveContext, Task> action)
    {
        EnsureServerStarted();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await action(db);
    }

    public async Task<TResult> WithDbContextAsync<TResult>(Func<CoveContext, Task<TResult>> action)
    {
        EnsureServerStarted();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        return await action(db);
    }

    // Dispose() comes through here too. The base stops the host first, so the database is dropped only
    // after the API has returned its connections.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        Task<CovePostgresDatabase>? database;
        lock (_databaseLock)
        {
            database = _database;
            _database = null;
            _databaseDisposed = true;
        }

        if (database is { IsCompletedSuccessfully: true })
            await database.Result.DisposeAsync();
    }

    private Task<CovePostgresDatabase> EnsureDatabaseAsync()
    {
        lock (_databaseLock)
        {
            ObjectDisposedException.ThrowIf(_databaseDisposed, this);
            return _database ??= CovePostgresDatabase.CreateAsync();
        }
    }

    private void EnsureServerStarted()
    {
        if (_serverStarted)
            return;

        lock (ServerStartEnvironmentLock)
        {
            if (_serverStarted)
                return;

            var previousAspNetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var previousDotNetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

            try
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _environmentName);
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _environmentName);
                StartServer();
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetEnvironment);
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotNetEnvironment);
            }

            _serverStarted = true;
        }
    }
}

file sealed class IntegrationTestTokenService : ITokenService, IExistingUserPrincipalResolver
{
    private static readonly CovePrincipal Principal = CreatePrincipal(
        CoveWebApplicationFactory.TestUserId,
        "integration-user");
    private static readonly CovePrincipal ScopedApiPrincipal = CreatePrincipal(
        CoveWebApplicationFactory.TestUserId,
        "integration-user",
        PrincipalKind.ApiToken,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "videos.read" });
    private static readonly CovePrincipal ImageDeleteWithoutFilePrincipal = CreatePrincipal(
        CoveWebApplicationFactory.TestUserId,
        "integration-user",
        PrincipalKind.ApiToken,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Permissions.ImagesDelete });

    public Task<TokenPair> IssueForUserAsync(int userId, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(new TokenPair(
            "integration-test-access",
            "integration-test-refresh",
            now.AddMinutes(15),
            now.AddDays(30),
            new UserDto(
                Id: CoveWebApplicationFactory.TestUserId,
                Username: "integration-user",
                DisplayName: null,
                Email: null,
                IsActive: true,
                IsLocked: false,
                IsSystem: true,
                MustChangePassword: false,
                HasPassword: true,
                LastLoginAt: now,
                LastLoginIp: ip,
                CreatedAt: now,
                Roles: [],
                UiPreferences: null)));
    }

    public Task<TokenPair> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task RevokeChainAsync(string refreshToken, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<CovePrincipal?> ResolveAsync(string? authorizationHeader, string? ip, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return Task.FromResult<CovePrincipal?>(null);

        return Task.FromResult<CovePrincipal?>(
            authorizationHeader.Contains("integration-scoped-token", StringComparison.Ordinal)
                ? ScopedApiPrincipal
                : authorizationHeader.Contains("integration-image-delete-without-file-token", StringComparison.Ordinal)
                    ? ImageDeleteWithoutFilePrincipal
                    : Principal);
    }

    public Task<CovePrincipal?> ResolveExistingUserAsync(int userId, string? ip, string? userAgent, CancellationToken ct = default)
        => Task.FromResult<CovePrincipal?>(userId switch
        {
            CoveWebApplicationFactory.TestUserId => Principal,
            CoveWebApplicationFactory.TestExternalUserId => CreatePrincipal(
                CoveWebApplicationFactory.TestExternalUserId,
                "integration-user-two"),
            _ => null,
        });

#pragma warning disable CS0618
    public Task<CovePrincipal?> ResolveExistingUserAsync(string username, string? ip, string? userAgent, CancellationToken ct = default)
        => Task.FromResult<CovePrincipal?>(null);
#pragma warning restore CS0618

    public Task<ApiTokenIssued> CreateApiTokenAsync(int userId, string name, IEnumerable<string>? scope, DateTime? expiresAt, CovePrincipal? actor, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task RevokeApiTokenAsync(Guid id, CovePrincipal? actor, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ApiTokenDto>> ListApiTokensAsync(int userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiTokenDto>>([]);

    private static CovePrincipal CreatePrincipal(
        int userId,
        string username,
        PrincipalKind kind = PrincipalKind.User,
        IReadOnlySet<string>? permissions = null) => new()
    {
        UserId = userId,
        Username = username,
        Kind = kind,
        Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        Permissions = permissions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Permissions.All },
        TokenId = kind == PrincipalKind.ApiToken ? Guid.Parse("11111111-1111-1111-1111-111111111111") : null,
    };
}
