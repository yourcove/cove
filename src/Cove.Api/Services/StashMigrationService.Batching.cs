using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    internal Func<Cove.Data.CoveContext, StringComparer, CancellationToken, Task<IStashFileClaims>> FileClaimsFactory { get; init; } = StashFileClaims.CreateAsync;

    // Only internal table names and integer boundaries enter these source SQL fragments.
    private static async IAsyncEnumerable<string> ReadSourceBatchesAsync(SqliteConnection conn, string table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var command = conn.CreateCommand();
            command.CommandText = $"SELECT id FROM {table} {(after.HasValue ? "WHERE id > $after" : "")} ORDER BY id LIMIT 250";
            command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
            var ids = new List<int>(250);
            using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
            if (ids.Count == 0) yield break;
            after = ids[^1];
            yield return string.Join(",", ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private async Task SeedImportFileClaimsAsync(IStashFileClaims claims, CancellationToken ct)
    {
        int? after = null;
        while (true)
        {
            var query = _db.Set<BaseFileEntity>().AsNoTracking();
            if (after.HasValue) query = query.Where(file => file.Id > after.Value);
            var files = await query.OrderBy(file => file.Id).Take(250)
                .Select(file => new { file.Id, file.ParentFolderId, file.Basename }).ToListAsync(ct);
            if (files.Count == 0) return;
            after = files[^1].Id;
            await claims.SeedAsync(files.Select(file => GetImportedBaseFileKey(file.ParentFolderId, file.Basename)), ct);
        }
    }
}
