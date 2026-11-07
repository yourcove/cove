using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.Tests;

/// <summary>
/// Records on the extension ABI that gained positional parameters in Cove 1.4 keep a Cove 1.3-shaped
/// constructor and Deconstruct for extensions compiled against 1.3. The build's ApiCompat gate only
/// checks against the latest release, so the 1.3 shapes are pinned here. The records then have more
/// than one public constructor, which System.Text.Json only accepts when one is marked
/// <see cref="JsonConstructorAttribute"/>.
/// </summary>
public class ExtensionAbiRecordCompatibilityTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static TheoryData<Type, Type[]> Cove13Shapes => new()
    {
        {
            typeof(UserUiPreferencesDto),
            [
                typeof(UserThemePreferencesDto), typeof(UserRatingSystemOptionsDto), typeof(UserTrackingPreferencesDto),
                typeof(UserVideosPreferencesDto), typeof(Dictionary<string, string>), typeof(UserPlaybackPreferencesDto),
                typeof(string), typeof(Dictionary<string, string>),
            ]
        },
        {
            typeof(PerformerSummaryDto),
            [
                typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string),
                typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            ]
        },
        {
            typeof(JobInfo),
            [
                typeof(string), typeof(string), typeof(string), typeof(JobStatus), typeof(double), typeof(string),
                typeof(DateTime), typeof(DateTime?), typeof(string), typeof(int?), typeof(int?), typeof(int?),
                typeof(int?), typeof(int?), typeof(string), typeof(double?), typeof(DateTime?),
            ]
        },
        {
            typeof(JobBatchResult),
            [typeof(int), typeof(int), typeof(int), typeof(int), typeof(IReadOnlyList<string>)]
        },
    };

    [Theory]
    [MemberData(nameof(Cove13Shapes))]
    public void Record_KeepsCove13ConstructorAndDeconstruct(Type recordType, Type[] parameterTypes)
    {
        Assert.NotNull(recordType.GetConstructor(parameterTypes));
        Assert.NotNull(recordType.GetMethod(
            "Deconstruct", parameterTypes.Select(type => type.MakeByRefType()).ToArray()));
    }

    [Fact]
    public void UserUiPreferencesDto_DeserializesMembersAddedAfterCove13()
    {
        var restored = JsonSerializer.Deserialize<UserUiPreferencesDto>(
            """{"homePageContent":"rows","renderMarkdown":true,"backNavigationTarget":"previous","keyboardShortcuts":{"activePresetId":"cove:native","showChordHints":false}}""",
            Options);

        Assert.Equal("rows", restored?.HomePageContent);
        Assert.True(restored?.RenderMarkdown);
        Assert.Equal("previous", restored?.BackNavigationTarget);
        Assert.False(restored?.KeyboardShortcuts?.ShowChordHints);
    }

    [Fact]
    public void PerformerSummaryDto_DeserializesMembersAddedAfterCove13()
    {
        var restored = JsonSerializer.Deserialize<PerformerSummaryDto>(
            """{"id":7,"name":"Performer","favorite":true,"videoCount":3,"country":"CA","deathDate":"2001-02"}""",
            Options);

        Assert.Equal(7, restored?.Id);
        Assert.Equal(3, restored?.VideoCount);
        Assert.Equal("CA", restored?.Country);
        Assert.Equal("2001-02", restored?.DeathDate);
    }

    [Fact]
    public void JobInfo_DeserializesMembersAddedAfterCove13()
    {
        var restored = JsonSerializer.Deserialize<JobInfo>(
            """{"id":"job","type":"scan","description":"Scan","status":"completed","progress":1,"startedAt":"2026-09-14T00:00:00Z","unitsTotal":4,"resultUrl":"/api/result"}""",
            Options);

        Assert.Equal(JobStatus.Completed, restored?.Status);
        Assert.Equal(4, restored?.UnitsTotal);
        Assert.Equal("/api/result", restored?.ResultUrl);
    }

    [Fact]
    public void JobBatchResult_DeserializesMembersAddedAfterCove13()
    {
        var restored = JsonSerializer.Deserialize<JobBatchResult>(
            """{"totalUnits":3,"succeededUnits":1,"failedUnits":1,"skippedUnits":1,"failedUnitIds":["a"],"skippedUnitIds":["b"]}""",
            Options);

        Assert.Equal(["a"], restored?.FailedUnitIds);
        Assert.Equal(["b"], restored?.SkippedUnitIds);
    }

    [Fact]
    public void JobBatchResult_Cove13ConstructorHasNoSkippedUnitIds()
    {
        // Invoked through reflection, as an extension compiled against Cove 1.3 binds to this constructor.
        var result = (JobBatchResult)typeof(JobBatchResult)
            .GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int), typeof(IReadOnlyList<string>)])!
            .Invoke([2, 1, 0, 1, Array.Empty<string>()]);

        Assert.Empty(result.SkippedUnitIds);
    }

    [Fact]
    public void VideoDto_DeserializesPrimaryFileId()
    {
        var restored = JsonSerializer.Deserialize<VideoDto>(
            """{"id":5,"organized":false,"isVr":false,"urls":[],"tags":[],"performers":[],"files":[],"groups":[],"galleries":[],"remoteIds":[],"createdAt":"c","updatedAt":"u","primaryFileId":11}""",
            Options);

        Assert.Equal(5, restored?.Id);
        Assert.Equal(11, restored?.PrimaryFileId);
    }

    [Fact]
    public void VideoMergeDto_KeepsItsTwoParameterConstructorAndDeserializesInitProperties()
    {
        Assert.NotNull(typeof(VideoMergeDto).GetConstructor([typeof(int), typeof(List<int>)]));

        var restored = JsonSerializer.Deserialize<VideoMergeDto>(
            """{"targetId":5,"sourceIds":[7],"metadata":{"fields":{"title":"source"}},"fileHandling":{"mode":"remove","deleteFiles":true,"deleteGenerated":false}}""",
            Options);

        Assert.Equal("source", restored?.Metadata?.Fields?["title"]);
        Assert.Equal("remove", restored?.FileHandling?.Mode);
        Assert.True(restored?.FileHandling?.DeleteFiles);
        Assert.False(restored?.FileHandling?.DeleteGenerated);
        Assert.Null(JsonSerializer.Deserialize<VideoMergeDto>("""{"targetId":5,"sourceIds":[7]}""", Options)?.FileHandling);
    }

    [Fact]
    public void DuplicateResolveRequest_KeepsItsConstructorAndDeserializesMetadata()
    {
        Assert.NotNull(typeof(Cove.Api.Services.DuplicateResolveRequest).GetConstructor(
            [typeof(IReadOnlyList<int>), typeof(string), typeof(bool), typeof(bool)]));

        var restored = JsonSerializer.Deserialize<Cove.Api.Services.DuplicateResolveRequest>(
            """{"groupIds":[3],"action":"merge","metadata":{"tagIds":[9]}}""",
            Options);

        Assert.Equal("merge", restored?.Action);
        Assert.Equal([9], restored?.Metadata?.TagIds);
    }
}
