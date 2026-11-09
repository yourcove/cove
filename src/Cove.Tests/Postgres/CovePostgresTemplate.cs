using System.Runtime.ExceptionServices;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

[assembly: AssemblyFixture(typeof(Cove.Tests.Postgres.CovePostgresTemplate))]

namespace Cove.Tests.Postgres;

/// <summary>
/// The one database a test run migrates. Every <see cref="CovePostgresDatabase"/> is a copy of it, so a
/// test starts from the migrated schema without replaying every migration. It is created on first use,
/// so a run that never touches PostgreSQL, such as the Windows filesystem job, needs no server; the
/// assembly fixture only drops it when the run ends.
/// </summary>
public sealed class CovePostgresTemplate : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlTestDatabase? _template;
    private static ExceptionDispatchInfo? _failure;

    internal static async Task<string> GetNameAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _template) is { } existing)
            return existing.DatabaseName;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_template != null)
                return _template.DatabaseName;
            // Every later test would wait out the same connect timeout, so they share the first failure.
            _failure?.Throw();

            try
            {
                Volatile.Write(ref _template, await CreateAsync(cancellationToken));
                return _template!.DatabaseName;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<PostgreSqlTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        // Managed PostgreSQL and the all-in-one image run initdb with --locale=C; a server whose default locale is,
        // say, en_US.UTF-8 would let upper() and lower() fold characters that those installs leave alone.
        var template = await PostgreSqlTestDatabase.CreateWithManagedLocaleAsync("cove_tests_template", cancellationToken);
        try
        {
            await using (var dataSource = CovePostgresDatabase.CreateDataSource(template.ConnectionString))
            await using (var db = new CoveContext(CovePostgresDatabase.CreateOptions(dataSource)))
                await db.Database.MigrateAsync(cancellationToken);
            await template.SealAsTemplateAsync(cancellationToken);
            return template;
        }
        catch
        {
            try
            {
                await template.DisposeAsync();
            }
            catch
            {
                // Report why the template could not be built, not why it could not be dropped after that.
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _template, null) is { } template)
            await template.DisposeAsync();
    }
}
