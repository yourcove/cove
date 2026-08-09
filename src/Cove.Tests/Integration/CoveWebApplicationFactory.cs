using System.Net.Http.Headers;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cove.Tests.Integration;

public sealed class CoveWebApplicationFactory : WebApplicationFactory<Program>
{
    public const int TestUserId = 1;
    public const int TestExternalUserId = 2;

    private static readonly object ServerStartEnvironmentLock = new();

    private readonly string _environmentName;
    private readonly string _connectionString = $"Data Source=file:cove-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _connection;
    private bool _serverStarted;

    public CoveWebApplicationFactory(string environmentName = "IntegrationTest")
    {
        _environmentName = environmentName;
        _connection = CreateOpenConnection(_connectionString);
        UseKestrel(0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environmentName);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cove:Auth:Enabled"] = "true",
                ["Cove:Auth:JwtSecret"] = "integration-test-secret",
                ["Cove:Postgres:Managed"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITokenService>();
            services.RemoveAll<IExistingUserPrincipalResolver>();
            services.RemoveAll<CoveContext>();
            services.RemoveAll<DbContextOptions<CoveContext>>();
            services.RemoveAll<DbContext>();

            services.AddScoped<ITokenService, IntegrationTestTokenService>();
            services.AddScoped<IExistingUserPrincipalResolver>(provider =>
                (IExistingUserPrincipalResolver)provider.GetRequiredService<ITokenService>());
            services.AddScoped(_ => new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(_connectionString)
                .Options);
            services.AddScoped<CoveContext>(sp => new IntegrationTestCoveContext(
                sp.GetRequiredService<DbContextOptions<CoveContext>>(),
                sp.GetRequiredService<ICurrentPrincipalAccessor>()));
            services.AddScoped<DbContext>(sp => sp.GetRequiredService<CoveContext>());
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        EnsureServerStarted();

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "integration-test-token");
        return client;
    }

    public async Task ResetDatabaseAsync()
    {
        EnsureServerStarted();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

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
    }

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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _connection.Dispose();
    }

    private static SqliteConnection CreateOpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
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

file sealed class IntegrationTestCoveContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor)
    : CoveContext(options, principalAccessor)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
