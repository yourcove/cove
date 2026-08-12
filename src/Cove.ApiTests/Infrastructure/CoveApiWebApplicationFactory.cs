using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Cove.ApiTests.Infrastructure;

internal sealed class CoveApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _dataRoot;

    public CoveApiWebApplicationFactory(string connectionString, string dataRoot)
    {
        _connectionString = connectionString;
        _dataRoot = dataRoot;
        UseKestrel(0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationStartup");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cove:Auth:Enabled"] = "true",
                ["Cove:Auth:JwtSecret"] = "cove-fluent-api-tests-only-jwt-secret-4b93f6f2",
                ["Cove:BackupPath"] = Path.Combine(_dataRoot, "backups"),
                ["Cove:CachePath"] = Path.Combine(_dataRoot, "cache"),
                ["Cove:ExtensionPaths:0"] = Path.Combine(_dataRoot, "plugins"),
                ["Cove:GeneratedPath"] = Path.Combine(_dataRoot, "generated"),
                ["Cove:Postgres:ConnectionString"] = _connectionString,
                ["Cove:Postgres:Managed"] = "false",
            });
        });
    }
}
