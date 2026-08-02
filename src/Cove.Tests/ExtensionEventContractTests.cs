using Cove.Api.Services;
using Cove.Core.Events;
using Cove.Plugins;
using Cove.Sdk;

namespace Cove.Tests;

public class ExtensionEventContractTests
{
    [Theory]
    [InlineData(EventType.AudioCreated, "audiocreated", "audio.created")]
    [InlineData(EventType.AudioUpdated, "audioupdated", "audio.updated")]
    [InlineData(EventType.AudioDeleted, "audiodeleted", "audio.deleted")]
    [InlineData(EventType.TextCreated, "textcreated", "text.created")]
    [InlineData(EventType.TextUpdated, "textupdated", "text.updated")]
    [InlineData(EventType.TextDeleted, "textdeleted", "text.deleted")]
    [InlineData(EventType.VideoUpdated, "video.updated", "video.updated")]
    [InlineData(EventType.RatingDeleted, "rating.deleted", "rating.deleted")]
    public void BridgePreservesWireNameAndProvidesCanonicalName(
        EventType type,
        string expectedWireName,
        string expectedCanonicalName)
    {
        Assert.Equal(expectedWireName, ExtensionEventBridge.MapEventType(type));
        Assert.Equal(expectedCanonicalName, ExtensionEventBridge.MapCanonicalEventType(type));
    }

    [Fact]
    public async Task EventExtensionBaseRoutesCanonicalAudioNameFromLegacyWireEvent()
    {
        var extension = new RecordingEventExtension();
        var evt = new ExtensionEvent("audioupdated", "audio", 7)
        {
            CanonicalEventType = "audio.updated",
        };

        await extension.OnEventAsync(evt);

        Assert.Equal(["canonical"], extension.Calls);
    }

    [Fact]
    public async Task EventExtensionBaseStillRoutesExplicitLegacyHandler()
    {
        var extension = new LegacyRecordingEventExtension();
        var evt = new ExtensionEvent("audioupdated", "audio", 7)
        {
            CanonicalEventType = "audio.updated",
        };

        await extension.OnEventAsync(evt);

        Assert.Equal(["legacy"], extension.Calls);
    }

    private sealed class RecordingEventExtension : EventExtensionBase
    {
        public List<string> Calls { get; } = [];
        public override string Id => "test.canonical-events";
        public override string Name => "Canonical event test";
        public override string Version => "1.0.0";

        protected override void RegisterHandlers()
            => OnUpdated("audio", (_, _) =>
            {
                Calls.Add("canonical");
                return Task.CompletedTask;
            });
    }

    private sealed class LegacyRecordingEventExtension : EventExtensionBase
    {
        public List<string> Calls { get; } = [];
        public override string Id => "test.legacy-events";
        public override string Name => "Legacy event test";
        public override string Version => "1.0.0";

        protected override void RegisterHandlers()
            => On("audioupdated", (_, _) =>
            {
                Calls.Add("legacy");
                return Task.CompletedTask;
            });
    }
}
