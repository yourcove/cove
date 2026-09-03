using Cove.Api.Controllers;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class RelatedEntityFilterTests
{
    [Fact]
    public async Task PerformersCanRequireARelatedTaggedVideoAlongsideAPerformerFilter()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        fixture.Performers[0].Gender = GenderEnum.Female;
        fixture.Performers[1].Gender = GenderEnum.Female;
        fixture.Performers[2].Gender = GenderEnum.Male;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                GenderCriterion = new StringCriterion
                {
                    Modifier = CriterionModifier.MatchesRegex,
                    Value = "^(?:Female)$",
                },
                VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                {
                    ObjectFilter = new VideoFilter
                    {
                        TagsCriterion = new MultiIdCriterion
                        {
                            Modifier = CriterionModifier.IncludesAll,
                            Value = [202],
                        },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Theory]
    [InlineData("videos", 402, 403)]
    [InlineData("images", 502, 503)]
    [InlineData("galleries", 602, 603)]
    [InlineData("audios", 802, 803)]
    [InlineData("texts", 902, 903)]
    public async Task PerformerBearingMediaCanUseAPerformerFilter(string entityType, int firstExpectedId, int secondExpectedId)
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var relatedFilter = new RelatedFilterCriterion<PerformerFilter>
        {
            ObjectFilter = new PerformerFilter
            {
                NameCriterion = new StringCriterion
                {
                    Modifier = CriterionModifier.Includes,
                    Value = "Bianca",
                },
            },
        };

        var actualIds = await QueryMediaIdsAsync(fixture, entityType, relatedFilter);

        Assert.Equal([firstExpectedId, secondExpectedId], actualIds);
    }

    [Fact]
    public async Task AudioCanUseFavoritePerformersInsteadOfASpecializedFavoriteCriterion()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        fixture.Performers[1].Favorite = true;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actualIds = await QueryMediaIdsAsync(fixture, "audios", new RelatedFilterCriterion<PerformerFilter>
        {
            ObjectFilter = new PerformerFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
        });

        Assert.Equal([802, 803], actualIds);
    }

    [Theory]
    [InlineData("videos")]
    [InlineData("images")]
    [InlineData("galleries")]
    [InlineData("audios")]
    [InlineData("texts")]
    public async Task MediaAggregatesUseTheSameRelatedPerformerFilter(string entityType)
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        var relatedFilter = new RelatedFilterCriterion<PerformerFilter>
        {
            ObjectFilter = new PerformerFilter
            {
                NameCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Bianca" },
            },
        };

        var count = entityType switch
        {
            "videos" => (await new VideoRepository(fixture.Context).AggregateAsync(
                new VideoFilter { PerformerFilterCriterion = relatedFilter }, DefaultFindFilter(), TestContext.Current.CancellationToken)).Count,
            "images" => (await new ImageRepository(fixture.Context).AggregateAsync(
                new ImageFilter { PerformerFilterCriterion = relatedFilter }, DefaultFindFilter(), TestContext.Current.CancellationToken)).Count,
            "galleries" => (await new GalleryRepository(fixture.Context).AggregateAsync(
                new GalleryFilter { PerformerFilterCriterion = relatedFilter }, DefaultFindFilter(), TestContext.Current.CancellationToken)).Count,
            "audios" => ExtractValue(await new AudiosController(fixture.Context, null!, null!, null!, null!, null).Aggregate(
                new FilteredQueryRequest<AudioFilter>
                {
                    ObjectFilter = new AudioFilter { PerformerFilterCriterion = relatedFilter },
                    FindFilter = DefaultFindFilter(),
                }, TestContext.Current.CancellationToken)).Count,
            "texts" => ExtractValue(await new TextsController(fixture.Context, null!, null!, null!, null!, null!, null).Aggregate(
                new FilteredQueryRequest<TextDocumentFilter>
                {
                    ObjectFilter = new TextDocumentFilter { PerformerFilterCriterion = relatedFilter },
                    FindFilter = DefaultFindFilter(),
                }, TestContext.Current.CancellationToken)).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unsupported media type."),
        };

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RelatedPerformerFilterCanExcludeMatchingMedia()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var actualIds = await QueryMediaIdsAsync(fixture, "audios", new RelatedFilterCriterion<PerformerFilter>
        {
            Exclude = true,
            FindFilter = new FindFilter { Q = "Cora" },
        });

        Assert.Equal([801, 802], actualIds);
    }

    [Theory]
    [InlineData("videos", 401, 402)]
    [InlineData("images", 501, 502)]
    [InlineData("galleries", 601, 602)]
    [InlineData("audios", 801, 802)]
    [InlineData("texts", 901, 902)]
    public async Task MediaCanRequireEveryRelatedPerformerToMatch(string entityType, int firstExpectedId, int secondExpectedId)
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var actualIds = await QueryMediaIdsAsync(fixture, entityType, new RelatedFilterCriterion<PerformerFilter>
        {
            Mode = RelatedFilterMode.Every,
            ObjectFilter = new PerformerFilter
            {
                HeightCriterion = new IntCriterion { Modifier = CriterionModifier.LessThan, Value = 175 },
            },
        });

        Assert.Equal([firstExpectedId, secondExpectedId], actualIds);
    }

    [Theory]
    [InlineData("videos", 401, 402)]
    [InlineData("images", 501, 502)]
    [InlineData("galleries", 601, 602)]
    [InlineData("audios", 801, 802)]
    [InlineData("texts", 901, 902)]
    public async Task MediaCanRequireNoRelatedPerformerToMatch(string entityType, int firstExpectedId, int secondExpectedId)
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var actualIds = await QueryMediaIdsAsync(fixture, entityType, new RelatedFilterCriterion<PerformerFilter>
        {
            Mode = RelatedFilterMode.None,
            FindFilter = new FindFilter { Q = "Cora" },
        });

        Assert.Equal([firstExpectedId, secondExpectedId], actualIds);
    }

    [Fact]
    public async Task PerformersCanRequireARelatedFavoriteVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        var videoAffinities = await fixture.Context.UserEntityAffinities
            .Where(affinity => affinity.HostType == AffinityHostType.Video)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var affinity in videoAffinities)
            affinity.IsFavorite = affinity.HostId == 402;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                {
                    ObjectFilter = new VideoFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformersCanRequireARelatedFiveStarVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        var rating = await fixture.Context.Ratings.SingleAsync(
            item => item.HostType == RatingHostType.Video && item.HostId == 403,
            TestContext.Current.CancellationToken);
        rating.Value = 100;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                {
                    ObjectFilter = new VideoFilter
                    {
                        RatingCriterion = new IntCriterion
                        {
                            Modifier = CriterionModifier.Equals,
                            Value = 100,
                        },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformersCanRequireEveryRelatedVideoToMatch()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                {
                    Mode = RelatedFilterMode.Every,
                    ObjectFilter = new VideoFilter
                    {
                        DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 20 },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformersCanSearchRelatedAudios()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter>
                {
                    FindFilter = new FindFilter { Q = "Beta Audio" },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformersCanRequireARelatedAudioWithACodec()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        fixture.Context.Performers.Add(new Performer { Id = 304, Name = "No audios" });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter>
                {
                    ObjectFilter = new AudioFilter
                    {
                        AudioCodecCriterion = new StringCriterion
                        {
                            Modifier = CriterionModifier.NotNull,
                            Value = string.Empty,
                        },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformersCanRequireEveryRelatedAudioToMatch()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter>
                {
                    Mode = RelatedFilterMode.Every,
                    ObjectFilter = new AudioFilter
                    {
                        DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 100 },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task NoRelatedAudioModeRequiresAtLeastOneAudio()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        fixture.Context.Performers.Add(new Performer { Id = 304, Name = "No audios" });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter>
                {
                    Mode = RelatedFilterMode.None,
                    ObjectFilter = new AudioFilter
                    {
                        DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 200 },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformerFilterExpressionCombinesRepeatedRelatedAudioCriteria()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            null,
            DefaultFindFilter(),
            TestContext.Current.CancellationToken,
            new FilterExpression<PerformerFilter>
            {
                Operator = FilterExpressionOperator.And,
                Children =
                [
                    new() { Filter = new PerformerFilter { AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter> { FindFilter = new FindFilter { Q = "Alpha Audio" } } } },
                    new() { Filter = new PerformerFilter { AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter> { FindFilter = new FindFilter { Q = "Gamma Audio" } } } },
                ],
            });

        Assert.Equal([301], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task PerformerFilterExpressionCombinesRepeatedRelatedVideoCriteria()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        var disallowedPerformer = new Performer { Id = 304, Name = "Disallowed category" };
        fixture.Context.Videos.AddRange(
            new Video
            {
                Id = 404,
                Title = "Required category video",
                VideoTags = [new VideoTag { TagId = 202 }],
                VideoPerformers = [new VideoPerformer { Performer = disallowedPerformer }],
            },
            new Video
            {
                Id = 405,
                Title = "Disallowed category video",
                VideoTags = [new VideoTag { TagId = 201 }],
                VideoPerformers = [new VideoPerformer { Performer = disallowedPerformer }],
            });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            null,
            DefaultFindFilter(),
            TestContext.Current.CancellationToken,
            new FilterExpression<PerformerFilter>
            {
                Operator = FilterExpressionOperator.And,
                Children =
                [
                    new()
                    {
                        Filter = new PerformerFilter
                        {
                            VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                            {
                                Mode = RelatedFilterMode.Every,
                                ObjectFilter = new VideoFilter
                                {
                                    TagsCriterion = new MultiIdCriterion
                                    {
                                        Modifier = CriterionModifier.Includes,
                                        Value = [202, 203],
                                    },
                                },
                            },
                        },
                    },
                    new()
                    {
                        Filter = new PerformerFilter
                        {
                            VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                            {
                                ObjectFilter = new VideoFilter
                                {
                                    TagsCriterion = new MultiIdCriterion
                                    {
                                        Modifier = CriterionModifier.Includes,
                                        Value = [202],
                                    },
                                },
                            },
                        },
                    },
                ],
            });

        Assert.Equal([301, 302], result.Items.Select(performer => performer.Id).ToArray());
    }

    [Fact]
    public async Task NoRelatedVideoModeRequiresAtLeastOneVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        fixture.Context.Performers.Add(new Performer { Id = 304, Name = "No videos" });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            new PerformerFilter
            {
                VideoFilterCriterion = new RelatedFilterCriterion<VideoFilter>
                {
                    Mode = RelatedFilterMode.None,
                    ObjectFilter = new VideoFilter
                    {
                        DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 40 },
                    },
                },
            },
            DefaultFindFilter(),
            TestContext.Current.CancellationToken);

        Assert.Equal([301, 302, 303], result.Items.Select(performer => performer.Id).ToArray());
    }

    private static async Task<int[]> QueryMediaIdsAsync(
        EntityListSortBehaviorHarnessTests.SortHarnessFixture fixture,
        string entityType,
        RelatedFilterCriterion<PerformerFilter> relatedFilter)
    {
        var findFilter = DefaultFindFilter();
        return entityType switch
        {
            "videos" => (await new VideoRepository(fixture.Context).FindAsync(
                new VideoFilter { PerformerFilterCriterion = relatedFilter }, findFilter, TestContext.Current.CancellationToken))
                .Items.Select(item => item.Id).ToArray(),
            "images" => (await new ImageRepository(fixture.Context).FindAsync(
                new ImageFilter { PerformerFilterCriterion = relatedFilter }, findFilter, TestContext.Current.CancellationToken))
                .Items.Select(item => item.Id).ToArray(),
            "galleries" => (await new GalleryRepository(fixture.Context).FindAsync(
                new GalleryFilter { PerformerFilterCriterion = relatedFilter }, findFilter, TestContext.Current.CancellationToken))
                .Items.Select(item => item.Id).ToArray(),
            "audios" => ExtractItems(await new AudiosController(fixture.Context, null!, null!, null!, null!, null).FindPost(
                new FilteredQueryRequest<AudioFilter>
                {
                    ObjectFilter = new AudioFilter { PerformerFilterCriterion = relatedFilter },
                    FindFilter = findFilter,
                }, TestContext.Current.CancellationToken)).Select(item => item.Id).ToArray(),
            "texts" => ExtractItems(await new TextsController(fixture.Context, null!, null!, null!, null!, null!, null).FindPost(
                new FilteredQueryRequest<TextDocumentFilter>
                {
                    ObjectFilter = new TextDocumentFilter { PerformerFilterCriterion = relatedFilter },
                    FindFilter = findFilter,
                }, TestContext.Current.CancellationToken)).Select(item => item.Id).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unsupported media type."),
        };
    }

    private static FindFilter DefaultFindFilter() => new()
    {
        Page = 1,
        PerPage = 50,
        Sort = "created_at",
        Direction = Cove.Core.Enums.SortDirection.Asc,
    };

    private static IReadOnlyList<T> ExtractItems<T>(ActionResult<PaginatedResponse<T>> actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        return Assert.IsType<PaginatedResponse<T>>(ok.Value).Items;
    }

    private static T ExtractValue<T>(ActionResult<T> actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        return Assert.IsType<T>(ok.Value);
    }
}
