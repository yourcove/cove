using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Pgvector;

namespace Cove.Tests;

public class FileIndexConfigurationTests
{
    [Theory]
    [InlineData(typeof(VideoFile), "VideoId", "Path", "\"VideoId\" IS NOT NULL")]
    [InlineData(typeof(ImageFile), "ImageId", "Path", "\"ImageId\" IS NOT NULL")]
    [InlineData(typeof(ImageFile), "ImageId", "Basename", "\"ImageId\" IS NOT NULL")]
    [InlineData(typeof(GalleryFile), "GalleryId", "Path", "\"GalleryId\" IS NOT NULL")]
    [InlineData(typeof(AudioFile), "AudioId", "Path", "\"AudioId\" IS NOT NULL")]
    [InlineData(typeof(TextFile), "TextDocumentId", "Path", "\"TextDocumentId\" IS NOT NULL")]
    public void MediaSpecificFileIndexesExcludeOtherFileTypes(
        Type entityType,
        string foreignKey,
        string secondaryProperty,
        string expectedFilter)
    {
        using var context = CreateContext();
        var index = Assert.Single(
            context.Model.FindEntityType(entityType)!.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name).SequenceEqual([foreignKey, secondaryProperty]));

        Assert.Equal(expectedFilter, index.GetFilter());
    }

    [Fact]
    public void GalleryFileIndexCoversAggregateAndModificationTimeColumns()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var index = Assert.Single(
            model.FindEntityType(typeof(GalleryFile))!.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(["GalleryId", "Path"]));

        Assert.Equal(new[] { "Size", "ModTime" }, Assert.IsType<string[]>(index["Npgsql:IndexInclude"]));
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=cove_model_tests;Username=postgres;Password=postgres",
                options => options.UseVector())
            .Options;
        return new CoveContext(options);
    }
}
