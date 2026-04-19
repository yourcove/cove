using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Cove.Core.Entities;
using Cove.Data;

namespace Cove.Tests;

public class RemoteIdMappingTests
{
    [Fact]
    public void RemoteIdEntities_UseCompatibilityTableNames()
    {
        using var context = CreateContext();

        Assert.Equal("SceneRemoteId", GetEntityType(context, typeof(SceneRemoteId)).GetTableName());
        Assert.Equal("PerformerRemoteId", GetEntityType(context, typeof(PerformerRemoteId)).GetTableName());
        Assert.Equal("TagRemoteId", GetEntityType(context, typeof(TagRemoteId)).GetTableName());
        Assert.Equal("StudioRemoteId", GetEntityType(context, typeof(StudioRemoteId)).GetTableName());
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

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }
}