using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Filters;

public sealed class StringCollectionOperatorApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    [CoversEndpoint("PUT", "/api/groups/{id:int}/query")]
    public async Task GivenVideosWithDifferentUrlCollections_WhenEachStringOperatorIsUsed_ThenCollectionSemanticsArePreserved()
    {
        var owner = AsUser();
        var exact = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle("Exact URL collection")
            .WithUrl("https://needle.example")
            .Build(), TestContext.Current.CancellationToken);
        var mixed = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle("Mixed URL collection")
            .WithUrl("https://other.example")
            .WithUrl("https://needle.example/suffix")
            .Build(), TestContext.Current.CancellationToken);
        var other = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle("Other URL collection")
            .WithUrl("https://unrelated.example")
            .Build(), TestContext.Current.CancellationToken);
        var empty = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle("Empty URL collection")
            .Build(), TestContext.Current.CancellationToken);
        var ids = new[] { exact.Id, mixed.Id, other.Id, empty.Id };

        var cases = new[]
        {
            new OperatorCase(CriterionModifier.Equals, "https://needle.example", new[] { exact.Id }),
            new OperatorCase(CriterionModifier.NotEquals, "https://needle.example", new[] { mixed.Id, other.Id, empty.Id }),
            new OperatorCase(CriterionModifier.Includes, "NEEDLE.EXAMPLE", new[] { exact.Id, mixed.Id }),
            new OperatorCase(CriterionModifier.Excludes, "NEEDLE.EXAMPLE", new[] { other.Id, empty.Id }),
            new OperatorCase(CriterionModifier.MatchesRegex, "^https://needle\\.example", new[] { exact.Id, mixed.Id }),
            new OperatorCase(CriterionModifier.NotMatchesRegex, "^https://needle\\.example", new[] { other.Id, empty.Id }),
            new OperatorCase(CriterionModifier.IsNull, null, new[] { empty.Id }),
            new OperatorCase(CriterionModifier.NotNull, null, new[] { exact.Id, mixed.Id, other.Id }),
        };

        foreach (var testCase in cases)
        {
            var result = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
            {
                ObjectFilter = new VideoFilter
                {
                    Ids = [.. ids],
                    UrlCriterion = new StringCriterion
                    {
                        Modifier = testCase.Modifier,
                        Value = testCase.Value ?? string.Empty,
                    },
                },
                FindFilter = new FindFilter { PerPage = 20 },
            }, TestContext.Current.CancellationToken);

            result.Items.Select(video => video.Id).Should().BeEquivalentTo(testCase.ExpectedIds);
        }

        var dynamicGroup = await owner.CreateGroupAsync("Dynamic URL collection filter", TestContext.Current.CancellationToken);
        var queryJson = JsonSerializer.Serialize(new
        {
            entityType = "video",
            objectFilter = new VideoFilter
            {
                Ids = [.. ids],
                UrlCriterion = new StringCriterion
                {
                    Modifier = CriterionModifier.Equals,
                    Value = "https://needle.example",
                },
            },
        });

        await owner.UpdateGroupQueryAsync(
            dynamicGroup.Id,
            new GroupQueryUpdateDto("filter", queryJson),
            TestContext.Current.CancellationToken);
        var resolvedGroup = await owner.GetGroupByIdAsync(dynamicGroup.Id, TestContext.Current.CancellationToken);

        resolvedGroup.ItemCount.Should().Be(1);
        resolvedGroup.VideoCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/find")]
    [CoversEndpoint("POST", "/api/texts/find")]
    [CoversEndpoint("POST", "/api/studios/find")]
    [CoversEndpoint("POST", "/api/groups/find")]
    [CoversEndpoint("GET", "/api/segments")]
    [CoversEndpoint("PUT", "/api/groups/{id:int}/query")]
    public async Task GivenDistinctCollectionConsumers_WhenFilteredThroughTheApi_ThenEachWiringShapeUsesCollectionSemantics()
    {
        var fixture = await AsDbUser().SeedStringCollectionOperatorFixtureAsync(TestContext.Current.CancellationToken);
        var owner = AsUser();

        await AssertAudioFilterAsync(new AudioFilter
        {
            TrackTitleCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = "Needle track" },
        });
        await AssertAudioFilterAsync(new AudioFilter
        {
            FormatCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^flac$" },
        });
        await AssertAudioFilterAsync(new AudioFilter
        {
            AudioCodecCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^flac$" },
        });

        var text = await owner.FindTextsAsync(new FilteredQueryRequest<TextDocumentFilter>
        {
            Ids = [fixture.MatchingTextId, fixture.OtherTextId],
            ObjectFilter = new TextDocumentFilter
            {
                FormatCriterion = new StringCriterion { Modifier = CriterionModifier.NotMatchesRegex, Value = "^pdf$" },
            },
        }, TestContext.Current.CancellationToken);
        text.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.MatchingTextId);

        var studios = await owner.FindStudiosAsync(new FilteredQueryRequest<StudioFilter>
        {
            ObjectFilter = new StudioFilter
            {
                AliasesCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^needle alias$" },
            },
            FindFilter = new FindFilter { Q = "Alias collection studio" },
        }, TestContext.Current.CancellationToken);
        studios.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.AliasStudioId);

        var groups = await owner.FindGroupsAsync(new FilteredQueryRequest<GroupFilter>
        {
            ObjectFilter = new GroupFilter
            {
                AllowedHostTypesCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^gallery$" },
            },
            FindFilter = new FindFilter { Q = "Host type collection group" },
        }, TestContext.Current.CancellationToken);
        groups.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.HostTypeGroupId);

        var regexSegments = await owner.FindSegmentsByTitleAsync("^needle", "MATCHES_REGEX", TestContext.Current.CancellationToken);
        regexSegments.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.MatchingSegmentId);
        var nullSegments = await owner.FindSegmentsByTitleAsync(null, "IS_NULL", TestContext.Current.CancellationToken);
        nullSegments.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.EmptySegmentId);

        await AssertDynamicCountAsync("audio", new AudioFilter
        {
            FormatCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^flac$" },
        });
        await AssertDynamicCountAsync("audio", new AudioFilter
        {
            AudioCodecCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^flac$" },
        });
        await AssertDynamicCountAsync("text", new TextDocumentFilter
        {
            FormatCriterion = new StringCriterion { Modifier = CriterionModifier.MatchesRegex, Value = "^epub$" },
        });

        async Task AssertAudioFilterAsync(AudioFilter objectFilter)
        {
            var result = await owner.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
            {
                Ids = [fixture.MatchingAudioId, fixture.OtherAudioId],
                ObjectFilter = objectFilter,
            }, TestContext.Current.CancellationToken);
            result.Items.Should().ContainSingle().Which.Id.Should().Be(fixture.MatchingAudioId);
        }

        async Task AssertDynamicCountAsync(string entityType, object objectFilter)
        {
            var group = await owner.CreateGroupAsync($"Dynamic {entityType} collection filter {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
            var queryJson = JsonSerializer.Serialize(new { entityType, objectFilter });
            await owner.UpdateGroupQueryAsync(group.Id, new GroupQueryUpdateDto("filter", queryJson), TestContext.Current.CancellationToken);
            var resolved = await owner.GetGroupByIdAsync(group.Id, TestContext.Current.CancellationToken);
            resolved.ItemCount.Should().Be(1);
        }
    }

    private sealed record OperatorCase(
        CriterionModifier Modifier,
        string? Value,
        IReadOnlyCollection<int> ExpectedIds);
}
