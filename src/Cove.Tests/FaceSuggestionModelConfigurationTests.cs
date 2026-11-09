using Cove.Core.Entities;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

[RequiresPostgres]
public class FaceSuggestionModelConfigurationTests
{
    [Fact]
    public async Task FaceSuggestionDecision_UsesConfiguredTableAndIndexes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var context = database.CreateContext();

        var entityType = context.Model.FindEntityType(typeof(FaceSuggestionDecision));

        Assert.NotNull(entityType);
        Assert.Equal("face_suggestion_decisions", entityType!.GetTableName());
        Assert.Equal(16, entityType.FindProperty(nameof(FaceSuggestionDecision.Decision))!.GetMaxLength());

        var indexes = entityType.GetIndexes()
            .Select(index => (Properties: string.Join(",", index.Properties.Select(property => property.Name)), index.IsUnique))
            .ToList();

        Assert.Contains(("FaceId,PerformerId,UserId", true), indexes);
        Assert.Contains(("FaceId,UserId", false), indexes);
        Assert.Contains(("UserId,Decision", false), indexes);

        // The migrated schema must agree with the model.
        var decisionMaxLength = await context.Database
            .SqlQueryRaw<int?>("""
                SELECT character_maximum_length AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'face_suggestion_decisions' AND column_name = 'Decision'
                """)
            .SingleAsync(ct);
        Assert.Equal(16, decisionMaxLength);

        var schemaIndexes = await context.Database
            .SqlQueryRaw<SchemaIndex>("""
                SELECT string_agg(a.attname, ',' ORDER BY k.ordinality) AS "Columns", ix.indisunique AS "IsUnique"
                FROM pg_index ix
                JOIN pg_class t ON t.oid = ix.indrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality)
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
                WHERE n.nspname = 'public' AND t.relname = 'face_suggestion_decisions' AND NOT ix.indisprimary
                GROUP BY ix.indexrelid, ix.indisunique
                """)
            .ToListAsync(ct);
        var schemaIndexKeys = schemaIndexes.Select(index => (index.Columns, index.IsUnique)).ToList();

        Assert.Contains(("FaceId,PerformerId,UserId", true), schemaIndexKeys);
        Assert.Contains(("FaceId,UserId", false), schemaIndexKeys);
        Assert.Contains(("UserId,Decision", false), schemaIndexKeys);
    }

    private sealed record SchemaIndex(string Columns, bool IsUnique);
}
