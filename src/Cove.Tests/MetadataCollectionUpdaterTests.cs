using Cove.Api.Helpers;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Tests;

public class MetadataCollectionUpdaterTests
{
    [Fact]
    public void SetOnlyChangesDifferentCollections()
    {
        var original = new VideoTag { TagId = 1 };
        ICollection<VideoTag> tags = [original, new VideoTag { TagId = 2 }];

        Assert.False(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [2, 1], BulkUpdateMode.Set, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.Contains(original, tags);

        Assert.True(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [2, 3, 3], BulkUpdateMode.Set, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.Equal([2, 3], tags.Select(item => item.TagId));
    }

    [Fact]
    public void AddOnlyReportsAndCreatesMissingValues()
    {
        ICollection<VideoTag> tags = [new VideoTag { TagId = 1 }];

        Assert.False(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [1, 1], BulkUpdateMode.Add, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.True(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [2, 2], BulkUpdateMode.Add, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.Equal([1, 2], tags.Select(item => item.TagId));
    }

    [Fact]
    public void RemoveOnlyReportsExistingValues()
    {
        ICollection<VideoTag> tags = [new VideoTag { TagId = 1 }, new VideoTag { TagId = 2 }];

        Assert.False(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [3], BulkUpdateMode.Remove, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.True(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [1], BulkUpdateMode.Remove, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.Equal([2], tags.Select(item => item.TagId));
    }

    [Fact]
    public void UndefinedModeDoesNotChangeCollection()
    {
        var original = new VideoTag { TagId = 1 };
        ICollection<VideoTag> tags = [original];

        Assert.False(MetadataCollectionUpdater.ApplyBulkUpdate(tags, [1], (BulkUpdateMode)3, item => item.TagId, tagId => new VideoTag { TagId = tagId }));
        Assert.Equal([original], tags);
    }
}
