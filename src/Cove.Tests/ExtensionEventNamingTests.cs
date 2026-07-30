using Cove.Api.Middleware;
using Cove.Api.Services;
using Cove.Core.Events;

namespace Cove.Tests;

public class ExtensionEventNamingTests
{
    /// <summary>
    /// The names the bridge emitted before they were derived. Pinned so the derivation can never rename an
    /// event a subscriber is already written against.
    /// </summary>
    public static TheoryData<EventType, string> EstablishedNames => new()
    {
        { EventType.VideoCreated, "video.created" },
        { EventType.VideoUpdated, "video.updated" },
        { EventType.VideoDeleted, "video.deleted" },
        { EventType.PerformerCreated, "performer.created" },
        { EventType.PerformerUpdated, "performer.updated" },
        { EventType.PerformerDeleted, "performer.deleted" },
        { EventType.TagCreated, "tag.created" },
        { EventType.TagUpdated, "tag.updated" },
        { EventType.TagDeleted, "tag.deleted" },
        { EventType.TagMerged, "tag.merged" },
        { EventType.StudioCreated, "studio.created" },
        { EventType.StudioUpdated, "studio.updated" },
        { EventType.StudioDeleted, "studio.deleted" },
        { EventType.GalleryCreated, "gallery.created" },
        { EventType.GalleryUpdated, "gallery.updated" },
        { EventType.GalleryDeleted, "gallery.deleted" },
        { EventType.ImageCreated, "image.created" },
        { EventType.ImageUpdated, "image.updated" },
        { EventType.ImageDeleted, "image.deleted" },
        { EventType.GroupCreated, "group.created" },
        { EventType.GroupUpdated, "group.updated" },
        { EventType.GroupDeleted, "group.deleted" },
        { EventType.RatingCreated, "rating.created" },
        { EventType.RatingUpdated, "rating.updated" },
        { EventType.RatingDeleted, "rating.deleted" },
        { EventType.ScanStarted, "scan.started" },
        { EventType.ScanCompleted, "scan.completed" },
    };

    [Theory]
    [MemberData(nameof(EstablishedNames))]
    public void MapEventType_KeepsEveryEstablishedName(EventType type, string expected)
    {
        Assert.Equal(expected, ExtensionEventBridge.MapEventType(type));
    }

    [Fact]
    public void MapEventType_NamesEveryEventTypeAsNounDotVerb()
    {
        var dotless = Enum.GetValues<EventType>()
            .Select(type => (Type: type, Name: ExtensionEventBridge.MapEventType(type)))
            .Where(mapped => !mapped.Name.Contains('.'))
            .Select(mapped => $"{mapped.Type} → \"{mapped.Name}\"")
            .ToList();

        Assert.True(
            dotless.Count == 0,
            $"Every event must be addressable as noun.verb; these are not: {string.Join(", ", dotless)}");
    }

    [Fact]
    public void MapEventType_NamesAudioAndTextLikeEveryOtherEntity()
    {
        Assert.Equal("audio.created", ExtensionEventBridge.MapEventType(EventType.AudioCreated));
        Assert.Equal("audio.updated", ExtensionEventBridge.MapEventType(EventType.AudioUpdated));
        Assert.Equal("audio.deleted", ExtensionEventBridge.MapEventType(EventType.AudioDeleted));
        Assert.Equal("text.created", ExtensionEventBridge.MapEventType(EventType.TextCreated));
        Assert.Equal("text.updated", ExtensionEventBridge.MapEventType(EventType.TextUpdated));
        Assert.Equal("text.deleted", ExtensionEventBridge.MapEventType(EventType.TextDeleted));
    }

    [Fact]
    public void EveryRegisteredControllerResolvesItsFullLifecycle()
    {
        var unresolved = new List<string>();
        foreach (var entityType in EntityEventFilter.ControllerEntityMap.Values)
        {
            foreach (var operation in new[] { "created", "updated", "deleted" })
            {
                if (EntityEventFilter.GetEventType(entityType, operation) is null)
                    unresolved.Add($"{entityType}.{operation}");
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"A controller is registered for publishing but has no event type: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void GetEventType_ResolvesFromTheEnumNaming()
    {
        Assert.Equal(EventType.VideoCreated, EntityEventFilter.GetEventType("video", "created"));
        Assert.Equal(EventType.AudioUpdated, EntityEventFilter.GetEventType("audio", "updated"));
        Assert.Equal(EventType.TextDeleted, EntityEventFilter.GetEventType("text", "deleted"));
    }

    [Fact]
    public void GetEventType_ReturnsNullForAnEntityTheEnumDoesNotCover()
    {
        Assert.Null(EntityEventFilter.GetEventType("sandwich", "created"));
        Assert.Null(EntityEventFilter.GetEventType("video", "toasted"));
    }
}
