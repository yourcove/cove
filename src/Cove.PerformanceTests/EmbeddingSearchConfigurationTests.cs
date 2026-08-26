using System.Data.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Services;
using Cove.PerformanceTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pgvector;

namespace Cove.PerformanceTests;

[Collection("performance")]
public sealed class EmbeddingSearchConfigurationTests(PostgresPerformanceFixture fixture)
{
    [Theory]
    [InlineData(1, "100")]
    [InlineData(100, "300")]
    [InlineData(1_000_000_000, "1000")]
    public async Task KnnAsync_ParameterizesTransactionLocalEfSearchWithoutOverflow(int k, string expectedEfSearch)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var interceptor = new EfSearchCommandInterceptor();
        await using var db = fixture.CreateContext(interceptor);
        await db.Database.OpenConnectionAsync(cancellationToken);

        var sourceKey = $"ef-search-{k}";
        db.Embeddings.Add(new Embedding
        {
            HostType = EmbeddingHostType.Video,
            HostId = fixture.SampleVideoId,
            Kind = "performance.embedding",
            KindFamily = "feature.v1",
            Modality = EmbeddingModality.Visual,
            Dim = 3,
            Vector = new Vector(new float[] { 1f, 0f, 0f }),
            SectionIndex = 0,
            SourceKey = sourceKey,
        });
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS ix_embeddings_performance_feature_hnsw
            ON embeddings USING hnsw ((("Vector")::vector(3)) vector_cosine_ops)
            WHERE "Modality" = 1 AND "KindFamily" = 'feature.v1' AND "SectionIndex" = 0
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET hnsw.ef_search = 321", cancellationToken);

        var service = new EmbeddingService(db, []);
        var results = await service.KnnAsync(
            new Vector(new float[] { 1f, 0f, 0f }),
            k,
            new EmbeddingSearchOptions
            {
                HostType = EmbeddingHostType.Video,
                HostId = fixture.SampleVideoId,
                KindFamily = "feature.v1",
                Modality = EmbeddingModality.Visual,
                SourceKey = sourceKey,
                SectionIndex = 0,
            },
            cancellationToken);

        var command = Assert.Single(interceptor.Commands);
        Assert.Contains("set_config('hnsw.ef_search'", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedEfSearch, command.CommandText, StringComparison.Ordinal);
        Assert.Equal(expectedEfSearch, Assert.IsType<string>(Assert.Single(command.ParameterValues)));
        Assert.True(interceptor.KnnQueryCompleted);
        Assert.Equal(sourceKey, Assert.Single(results).Embedding.SourceKey);
        Assert.Equal("321", await db.Database.SqlQueryRaw<string>(
                "SELECT current_setting('hnsw.ef_search') AS \"Value\"")
            .SingleAsync(cancellationToken));
    }

    private sealed class EfSearchCommandInterceptor : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];
        public bool KnnQueryCompleted { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("set_config('hnsw.ef_search'", StringComparison.Ordinal))
            {
                Commands.Add(new CapturedCommand(
                    command.CommandText,
                    command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ORDER BY (\"Vector\")::vector(", StringComparison.Ordinal))
                KnnQueryCompleted = true;

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record CapturedCommand(string CommandText, IReadOnlyList<object?> ParameterValues);
}
