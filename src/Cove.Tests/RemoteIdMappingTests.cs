using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Cove.Core.Entities;
using Cove.Data;
using Pgvector.EntityFrameworkCore;

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

    // Builds production's PostgreSQL model without opening a connection, so this needs no server.
    private static CoveContext CreateContext()
        => new(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql("Host=localhost;Database=unused", options => options.UseVector())
            .Options);
}

