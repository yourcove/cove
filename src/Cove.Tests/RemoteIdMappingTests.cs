using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Cove.Core.Entities;
using Cove.Data;

namespace Cove.Tests;

public class RemoteIdMappingTests
{
    [Fact]
    public void RemoteIdEntities_UseSnakeCaseTableNames()
    {
        using var context = CreateContext();

        Assert.Equal("video_remote_ids", GetEntityType(context, typeof(VideoRemoteId)).GetTableName());
        Assert.Equal("performer_remote_ids", GetEntityType(context, typeof(PerformerRemoteId)).GetTableName());
        Assert.Equal("tag_remote_ids", GetEntityType(context, typeof(TagRemoteId)).GetTableName());
        Assert.Equal("studio_remote_ids", GetEntityType(context, typeof(StudioRemoteId)).GetTableName());
    }

    private static IEntityType GetEntityType(CoveContext context, Type entityType)
        => Assert.IsAssignableFrom<IEntityType>(context.Model.FindEntityType(entityType));

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"remote-id-mapping-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }
}

