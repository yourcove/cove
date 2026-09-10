using Cove.Data.Auth;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.ApiTests.Infrastructure;

public sealed class MetadataImportPostgresFixture : IAsyncLifetime
{
    private PostgreSqlTestDatabase database = null!;
    internal string ConnectionString => database.ConnectionString;

    internal CoveContext CreateContext(bool retry = false) => new(new DbContextOptionsBuilder<CoveContext>()
        .UseNpgsql(ConnectionString, options =>
        {
            options.UseVector();
            if (retry) options.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(20), null);
        }).Options);

    public async ValueTask InitializeAsync()
    {
        database = await PostgreSqlTestDatabase.CreateAsync();
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = AuthorizationSqlDefinitions.CreateFunctionsSql;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await database.DisposeAsync();
}
