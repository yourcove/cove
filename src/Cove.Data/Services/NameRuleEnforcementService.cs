using System.Data;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Data.Services;

public sealed class NameRuleUpgradeBlockedException(
    int tagGroupCount,
    int tagClaimCount,
    int performerGroupCount,
    int performerCandidateCount,
    int studioGroupCount,
    int studioCandidateCount)
    : InvalidOperationException(BuildMessage(
        tagGroupCount,
        tagClaimCount,
        performerGroupCount,
        performerCandidateCount,
        studioGroupCount,
        studioCandidateCount))
{
    public int TagGroupCount { get; } = tagGroupCount;
    public int TagClaimCount { get; } = tagClaimCount;
    public int PerformerGroupCount { get; } = performerGroupCount;
    public int PerformerCandidateCount { get; } = performerCandidateCount;
    public int StudioGroupCount { get; } = studioGroupCount;
    public int StudioCandidateCount { get; } = studioCandidateCount;
    public int UnresolvedGroupCount { get; } = tagGroupCount + performerGroupCount + studioGroupCount;
    public int UnresolvedClaimCount { get; } = tagClaimCount + performerCandidateCount + studioCandidateCount;

    private static string BuildMessage(
        int tagGroups,
        int tagClaims,
        int performerGroups,
        int performerCandidates,
        int studioGroups,
        int studioCandidates)
    {
        var totalGroups = tagGroups + performerGroups + studioGroups;
        var totalClaims = tagClaims + performerCandidates + studioCandidates;
        return $"Cove 1.3.0 cannot upgrade this database because {totalGroups} unresolved name conflict "
            + $"{(totalGroups == 1 ? "group" : "groups")} ({totalClaims} claims) remain: "
            + $"{tagGroups} tag, {performerGroups} performer, and {studioGroups} studio. Run the latest "
            + "Cove 1.2.x, open Settings → Operations → Name Conflicts, resolve them, and retry the upgrade. "
            + "Container users can temporarily select the latest 1.2.x image.";
    }
}

public sealed class NameRuleUpgradePreparation
{
    internal NameRuleUpgradePreparation(
        IReadOnlyList<NameRuleEnforcementService.TagStageRow> tags,
        IReadOnlyList<NameRuleEnforcementService.AliasStageRow> aliases,
        IReadOnlyList<NameRuleEnforcementService.PerformerStageRow> performers,
        IReadOnlyList<NameRuleEnforcementService.StudioStageRow> studios)
    {
        Tags = tags;
        Aliases = aliases;
        Performers = performers;
        Studios = studios;
    }

    internal IReadOnlyList<NameRuleEnforcementService.TagStageRow> Tags { get; }
    internal IReadOnlyList<NameRuleEnforcementService.AliasStageRow> Aliases { get; }
    internal IReadOnlyList<NameRuleEnforcementService.PerformerStageRow> Performers { get; }
    internal IReadOnlyList<NameRuleEnforcementService.StudioStageRow> Studios { get; }
}

/// <summary>
/// Bridges the 1.2 compatibility scanners and the 1.3 schema migration. PostgreSQL cannot
/// reproduce .NET Trim/ToLowerInvariant for every Unicode value, so Cove stages the exact
/// normalized display values and identity keys on the migration connection. The migration locks
/// and revalidates every original row before using that staging data, preventing a concurrent
/// writer from invalidating the preflight.
/// </summary>
public sealed class NameRuleEnforcementService(
    CoveContext db,
    TagNameConflictScanner tagScanner,
    EntityNameConflictScanner entityScanner)
{
    public const string MigrationId = "20260810170000_EnforceUniqueNames";
    public const string GuardFailureMarker = "COVE_NAME_RULE_GUARD";

    internal sealed record TagStageRow(int Id, string OriginalName, string NormalizedName, string NamespaceKey);
    internal sealed record AliasStageRow(int Id, int TagId, string OriginalAlias, string NormalizedAlias, string NamespaceKey);
    internal sealed record PerformerStageRow(
        int Id,
        string OriginalName,
        string? OriginalDisambiguation,
        string NormalizedName,
        string? NormalizedDisambiguation,
        string IdentityKey);
    internal sealed record StudioStageRow(int Id, string OriginalName, string NormalizedName, string NameKey);

    public Task<NameRuleUpgradePreparation> PreflightAsync(CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Name-rule upgrade preflight requires an unowned database connection.");

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return executionStrategy.ExecuteAsync(() => PreflightCoreAsync(ct));
    }

    private async Task<NameRuleUpgradePreparation> PreflightCoreAsync(CancellationToken ct)
    {
        // Keep all scanners and staging projections on one database snapshot. The migration later
        // locks and compares every original row against this snapshot before applying any changes.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var coreTableCount = await db.Database.SqlQueryRaw<int>("""
            SELECT (
                (to_regclass('public.tags') IS NOT NULL)::integer
                + (to_regclass('public.tag_aliases') IS NOT NULL)::integer
                + (to_regclass('public.performers') IS NOT NULL)::integer
                + (to_regclass('public.studios') IS NOT NULL)::integer
            ) AS "Value"
            """).SingleAsync(ct);
        if (coreTableCount == 0)
        {
            await transaction.CommitAsync(ct);
            return new NameRuleUpgradePreparation([], [], [], []);
        }

        if (coreTableCount != 4)
        {
            throw new InvalidOperationException(
                "The database contains only part of Cove's core name schema. Restore or migrate the core schema before running the Cove 1.3.0 name-rule preflight.");
        }

        var tagScan = await tagScanner.ScanWithoutImpactsAsync(ct);
        var performerScan = await entityScanner.ScanWithoutImpactsAsync(NameConflictEntityTypes.Performer, ct);
        var studioScan = await entityScanner.ScanWithoutImpactsAsync(NameConflictEntityTypes.Studio, ct);
        if (tagScan.UnresolvedGroupCount > 0
            || performerScan.UnresolvedGroupCount > 0
            || studioScan.UnresolvedGroupCount > 0)
        {
            throw CreateBlockedException(tagScan, performerScan, studioScan);
        }

        // Explicit projections intentionally avoid the key columns: they do not exist until this
        // enforcement migration succeeds, while every projected column exists at the 1.2 checkpoint.
        var tagRows = await db.Tags.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(tag => tag.Id)
            .Select(tag => new { tag.Id, tag.Name })
            .ToListAsync(ct);
        var aliasRows = await db.Set<TagAlias>().IgnoreQueryFilters().AsNoTracking()
            .OrderBy(alias => alias.Id)
            .Select(alias => new { alias.Id, alias.TagId, alias.Alias })
            .ToListAsync(ct);
        var performerRows = await db.Performers.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(performer => performer.Id)
            .Select(performer => new { performer.Id, performer.Name, performer.Disambiguation })
            .ToListAsync(ct);
        var studioRows = await db.Studios.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(studio => studio.Id)
            .Select(studio => new { studio.Id, studio.Name })
            .ToListAsync(ct);

        var tags = tagRows.Select(tag =>
        {
            var normalized = TagNameRules.NormalizeCanonicalName(tag.Name);
            return new TagStageRow(tag.Id, tag.Name, normalized, TagNameRules.NamespaceKey(normalized));
        }).ToArray();
        var aliases = aliasRows.Select(alias =>
        {
            var normalized = TagNameRules.NormalizeAlias(alias.Alias)
                ?? throw new NameRuleUpgradeBlockedException(1, 1, 0, 0, 0, 0);
            return new AliasStageRow(alias.Id, alias.TagId, alias.Alias, normalized, TagNameRules.NamespaceKey(normalized));
        }).ToArray();
        var performers = performerRows.Select(performer =>
        {
            var normalizedName = EntityNameRules.NormalizeCanonicalName(performer.Name);
            var normalizedDisambiguation = EntityNameRules.NormalizeDisambiguation(performer.Disambiguation);
            return new PerformerStageRow(
                performer.Id,
                performer.Name,
                performer.Disambiguation,
                normalizedName,
                normalizedDisambiguation,
                EntityNameRules.PerformerIdentityKey(normalizedName, normalizedDisambiguation));
        }).ToArray();
        var studios = studioRows.Select(studio =>
        {
            var normalizedName = EntityNameRules.NormalizeCanonicalName(studio.Name);
            return new StudioStageRow(
                studio.Id,
                studio.Name,
                normalizedName,
                EntityNameRules.StudioIdentityKey(normalizedName));
        }).ToArray();

        var duplicateTagKey = tags.Select(tag => tag.NamespaceKey)
            .Concat(aliases.Select(alias => alias.NamespaceKey))
            .GroupBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicatePerformerKey = performers
            .GroupBy(performer => performer.IdentityKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateStudioKey = studios
            .GroupBy(studio => studio.NameKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTagKey != null || duplicatePerformerKey != null || duplicateStudioKey != null)
        {
            throw new NameRuleUpgradeBlockedException(
                duplicateTagKey == null ? 0 : 1,
                duplicateTagKey?.Count() ?? 0,
                duplicatePerformerKey == null ? 0 : 1,
                duplicatePerformerKey?.Count() ?? 0,
                duplicateStudioKey == null ? 0 : 1,
                duplicateStudioKey?.Count() ?? 0);
        }

        var preparation = new NameRuleUpgradePreparation(tags, aliases, performers, studios);
        await transaction.CommitAsync(ct);
        return preparation;
    }

    private static NameRuleUpgradeBlockedException CreateBlockedException(
        Cove.Core.DTOs.TagNameConflictScanDto tags,
        Cove.Core.DTOs.EntityNameConflictScanDto performers,
        Cove.Core.DTOs.EntityNameConflictScanDto studios)
        => new(
            tags.UnresolvedGroupCount,
            tags.Groups.Sum(group => group.Claims.Count),
            performers.UnresolvedGroupCount,
            performers.Groups.Sum(group => group.Candidates.Count),
            studios.UnresolvedGroupCount,
            studios.Groups.Sum(group => group.Candidates.Count));

    public async Task<IAsyncDisposable> StageAsync(
        NameRuleUpgradePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Name-rule upgrade staging requires an unowned database connection.");

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_tags;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_aliases;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_performers;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_studios;
                    CREATE TEMP TABLE cove_name_rule_tags (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "NormalizedName" text NOT NULL,
                        "NamespaceKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT PRESERVE ROWS;
                    CREATE TEMP TABLE cove_name_rule_aliases (
                        "Id" integer PRIMARY KEY,
                        "TagId" integer NOT NULL,
                        "OriginalAlias" text NOT NULL,
                        "NormalizedAlias" text NOT NULL,
                        "NamespaceKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT PRESERVE ROWS;
                    CREATE TEMP TABLE cove_name_rule_performers (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "OriginalDisambiguation" text NULL,
                        "NormalizedName" text NOT NULL,
                        "NormalizedDisambiguation" text NULL,
                        "IdentityKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT PRESERVE ROWS;
                    CREATE TEMP TABLE cove_name_rule_studios (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "NormalizedName" text NOT NULL,
                        "NameKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT PRESERVE ROWS;
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }

            await CopyTagsAsync(connection, preparation.Tags, ct);
            await CopyAliasesAsync(connection, preparation.Aliases, ct);
            await CopyPerformersAsync(connection, preparation.Performers, ct);
            await CopyStudiosAsync(connection, preparation.Studios, ct);
            return new StagingScope(db, connection, openedHere);
        }
        catch
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public static bool IsGuardFailure(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(GuardFailureMarker, StringComparison.Ordinal);

    public static string GuardFailureMessage
        => "Cove 1.3.0 could not verify the tag, performer, and studio name rules because the database "
            + "changed during the upgrade preflight. No migration changes were applied. Run the latest "
            + "Cove 1.2.x, open Settings → Operations → Name Conflicts, confirm that the database is ready, "
            + "and retry the upgrade.";

    private static async Task CopyTagsAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<TagStageRow> rows,
        CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_name_rule_tags (\"Id\", \"OriginalName\", \"NormalizedName\", \"NamespaceKey\") FROM STDIN (FORMAT BINARY)",
            ct);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(row.Id, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(row.OriginalName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NormalizedName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NamespaceKey, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task CopyAliasesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<AliasStageRow> rows,
        CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_name_rule_aliases (\"Id\", \"TagId\", \"OriginalAlias\", \"NormalizedAlias\", \"NamespaceKey\") FROM STDIN (FORMAT BINARY)",
            ct);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(row.Id, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(row.TagId, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(row.OriginalAlias, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NormalizedAlias, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NamespaceKey, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task CopyPerformersAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<PerformerStageRow> rows,
        CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_name_rule_performers (\"Id\", \"OriginalName\", \"OriginalDisambiguation\", \"NormalizedName\", \"NormalizedDisambiguation\", \"IdentityKey\") FROM STDIN (FORMAT BINARY)",
            ct);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(row.Id, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(row.OriginalName, NpgsqlDbType.Text, ct);
            if (row.OriginalDisambiguation == null)
                await writer.WriteNullAsync(ct);
            else
                await writer.WriteAsync(row.OriginalDisambiguation, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NormalizedName, NpgsqlDbType.Text, ct);
            if (row.NormalizedDisambiguation == null)
                await writer.WriteNullAsync(ct);
            else
                await writer.WriteAsync(row.NormalizedDisambiguation, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.IdentityKey, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task CopyStudiosAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<StudioStageRow> rows,
        CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_name_rule_studios (\"Id\", \"OriginalName\", \"NormalizedName\", \"NameKey\") FROM STDIN (FORMAT BINARY)",
            ct);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(row.Id, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(row.OriginalName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NormalizedName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.NameKey, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private sealed class StagingScope(
        CoveContext db,
        NpgsqlConnection connection,
        bool closeConnection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_studios;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_performers;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_aliases;
                    DROP TABLE IF EXISTS pg_temp.cove_name_rule_tags;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            if (closeConnection && connection.State == ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
    }
}
