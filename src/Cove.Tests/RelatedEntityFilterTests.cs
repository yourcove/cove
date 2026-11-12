using Cove.Api.Controllers;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

[RequiresPostgres]
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
        fixture.AddFavorite(AffinityHostType.Performer, fixture.Performers[1].Id);
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

    [Fact]
    public async Task PerformersCanRequireTheirOwnOccurrenceTagOnARelatedVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var result = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });

        // The dominant performer shares the video but the tag belongs to the other performer's appearance.
        Assert.Equal([SubmissivePerformerId], result);
    }

    [Fact]
    public async Task PerformerAppearanceConditionsMatchTheSameRelatedVideoAsTheVideoFilter()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var result = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            ObjectFilter = new VideoFilter { TitleCriterion = TitleIncludes("Second") },
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });

        // The dominant performer is dominant in the first video and merely appears in the second.
        Assert.Equal([SubmissivePerformerId], result);
    }

    [Fact]
    public async Task PerformerAppearanceConditionsCanBeAlternativesToTheVideoFilter()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var withVideoFilter = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            ConditionOperator = RelatedFilterConditionOperator.Or,
            ObjectFilter = new VideoFilter { TitleCriterion = TitleIncludes("Solo appearance") },
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });
        var withoutVideoFilter = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            ConditionOperator = RelatedFilterConditionOperator.Or,
            AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 60 },
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });

        Assert.Equal([SubmissivePerformerId, SoloPerformerId], withVideoFilter);
        // An absent video filter is not an always-true alternative.
        Assert.Equal([SubmissivePerformerId], withoutVideoFilter);
    }

    [Fact]
    public async Task PerformersCanRequireTheirOccurrenceTagOnEveryOrNoRelatedVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var alwaysDominant = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            Mode = RelatedFilterMode.Every,
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });
        var neverSubmissive = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            Mode = RelatedFilterMode.None,
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });

        var alwaysDominantOrInSecondVideo = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            Mode = RelatedFilterMode.Every,
            ConditionOperator = RelatedFilterConditionOperator.Or,
            ObjectFilter = new VideoFilter { TitleCriterion = TitleIncludes("Second") },
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });
        var neverDominantInSecondVideo = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            Mode = RelatedFilterMode.None,
            ObjectFilter = new VideoFilter { TitleCriterion = TitleIncludes("Second") },
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });
        var legacyNotSubmissive = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            Exclude = true,
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });

        Assert.Equal([SoloPerformerId], alwaysDominant);
        // Everyone with a video except the submissive performer; the performer without videos stays out.
        Assert.Equal([301, 302, 303, DominantPerformerId, SoloPerformerId], neverSubmissive);
        Assert.Equal([DominantPerformerId, SoloPerformerId], alwaysDominantOrInSecondVideo);
        Assert.Equal([301, 302, 303, DominantPerformerId, SoloPerformerId], neverDominantInSecondVideo);
        // The legacy exclusion also keeps the performer without videos.
        Assert.Equal([301, 302, 303, DominantPerformerId, SoloPerformerId, 314], legacyNotSubmissive);
    }

    [Fact]
    public async Task PerformerOccurrenceTagConditionsExpandSubTags()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);
        fixture.Context.Tags.Add(new Tag { Id = 213, Name = "Role" });
        fixture.Context.Set<TagParent>().Add(new TagParent { ParentId = 213, ChildId = SubmissiveTagId });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var included = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [213], Depth = -1 },
        });
        var required = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, RequiredIds = [213], RequiredIdsDepth = -1 },
        });

        Assert.Equal([SubmissivePerformerId], included);
        Assert.Equal([SubmissivePerformerId], required);
    }

    [Fact]
    public async Task PerformersCanRequireTheirAgeOnARelatedVideo()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var young = new IntCriterion { Modifier = CriterionModifier.Between, Value = 18, Value2 = 25 };
        var inSecondVideo = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            ObjectFilter = new VideoFilter { TitleCriterion = TitleIncludes("Second") },
            AgeAtHostDateCriterion = young,
        });
        var whileDominant = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<VideoFilter>
        {
            AgeAtHostDateCriterion = young,
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });

        Assert.Equal([DominantPerformerId], inSecondVideo);
        // The submissive performer is dominant in one video, but aged 40 in it.
        Assert.Equal([DominantPerformerId, SoloPerformerId], whileDominant);
    }

    [Fact]
    public async Task PerformersCanRequireTheirOwnOccurrenceTagOnARelatedAudio()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);

        var atLeastOne = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });
        var sameAudio = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            ObjectFilter = new AudioFilter { TitleCriterion = TitleIncludes("Second") },
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });
        var alternatives = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            ConditionOperator = RelatedFilterConditionOperator.Or,
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });
        var alternativesWithAudioFilter = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            ConditionOperator = RelatedFilterConditionOperator.Or,
            ObjectFilter = new AudioFilter { TitleCriterion = TitleIncludes("Beta Audio") },
            PerformerOccurrenceTagsCriterion = Includes(SubmissiveTagId),
        });
        var every = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            Mode = RelatedFilterMode.Every,
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });
        var none = await FindPerformerIdsAsync(fixture, new RelatedFilterCriterion<AudioFilter>
        {
            Mode = RelatedFilterMode.None,
            PerformerOccurrenceTagsCriterion = Includes(DominantTagId),
        });

        Assert.Equal([DominantPerformerId], atLeastOne);
        Assert.Equal([SubmissivePerformerId], sameAudio);
        Assert.Equal([DominantPerformerId], alternatives);
        Assert.Equal([301, 302, DominantPerformerId], alternativesWithAudioFilter);
        Assert.Equal([SubmissivePerformerId], every);
        Assert.Contains(301, none);
        Assert.DoesNotContain(none, id => id >= DominantPerformerId);
    }

    [Fact]
    public async Task DistinctRelatedAudiosHonorPerformerOccurrenceTags()
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        await SeedPerformerAppearancesAsync(fixture);
        // Carries both tags on one audio and has a second, untagged audio: two audios, but not two matching ones.
        var bothOnOneAudio = new Performer { Id = 315, Name = "Both on one audio" };
        fixture.Context.Audios.AddRange(
            new Audio { Id = 823, Title = "Both tags audio", AudioPerformers = [new AudioPerformer { Performer = bothOnOneAudio }] },
            new Audio { Id = 824, Title = "Untagged audio", AudioPerformers = [new AudioPerformer { Performer = bothOnOneAudio }] });
        fixture.Context.TagApplications.AddRange(
            new TagApplication { HostType = AffinityHostType.Audio, HostId = 823, ContextType = "performer", ContextId = 315, TagId = DominantTagId, SourceKey = "test" },
            new TagApplication { HostType = AffinityHostType.Audio, HostId = 823, ContextType = "performer", ContextId = 315, TagId = SubmissiveTagId, SourceKey = "test" });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        static FilterExpressionNode<PerformerFilter> Appearance(int tagId) => new()
        {
            Filter = new PerformerFilter
            {
                AudioFilterCriterion = new RelatedFilterCriterion<AudioFilter> { PerformerOccurrenceTagsCriterion = Includes(tagId) },
            },
        };

        var result = await new PerformerRepository(fixture.Context).FindAsync(
            null,
            DefaultFindFilter(),
            TestContext.Current.CancellationToken,
            new FilterExpression<PerformerFilter>
            {
                Operator = FilterExpressionOperator.And,
                RelatedScope = new() { FilterKey = "audioFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
                Children = [Appearance(DominantTagId), Appearance(SubmissiveTagId)],
            });

        // Dominant in one audio and submissive in a different one; the submissive performer is only ever dominant.
        Assert.Equal([DominantPerformerId], result.Items.Select(performer => performer.Id).ToArray());
    }

    private const int DominantTagId = 211;
    private const int SubmissiveTagId = 212;
    private const int DominantPerformerId = 311;
    private const int SubmissivePerformerId = 312;
    private const int SoloPerformerId = 313;

    /// <summary>
    /// Adds performers whose occurrence tags differ within shared media. The dominant and submissive performers
    /// share two videos dated 2020-06-01, aged 20 and 40: in the first each carries the tag they are named after, in
    /// the second only the submissive performer is tagged, as dominant. The solo performer is dominant, aged 20, in
    /// a third video. They also share two audios: both are dominant in the first, and in the second the dominant
    /// performer is submissive while the submissive performer is dominant. A fourth performer appears in nothing.
    /// </summary>
    private static async Task SeedPerformerAppearancesAsync(EntityListSortBehaviorHarnessTests.SortHarnessFixture fixture)
    {
        var context = fixture.Context;
        var dominant = new Performer { Id = DominantPerformerId, Name = "Dominant", Birthdate = new DateOnly(2000, 1, 1) };
        var submissive = new Performer { Id = SubmissivePerformerId, Name = "Submissive", Birthdate = new DateOnly(1980, 1, 1) };
        var solo = new Performer { Id = SoloPerformerId, Name = "Solo", Birthdate = new DateOnly(2000, 1, 1) };
        context.Performers.Add(new Performer { Id = 314, Name = "No appearances" });
        context.Tags.AddRange(
            new Tag { Id = DominantTagId, Name = "Dominant" },
            new Tag { Id = SubmissiveTagId, Name = "Submissive" });
        var date = new DateOnly(2020, 6, 1);
        context.Videos.AddRange(
            new Video
            {
                Id = 421,
                Title = "First appearance video",
                Date = date,
                VideoPerformers = [new VideoPerformer { Performer = dominant }, new VideoPerformer { Performer = submissive }],
            },
            new Video
            {
                Id = 422,
                Title = "Second appearance video",
                Date = date,
                VideoPerformers = [new VideoPerformer { Performer = dominant }, new VideoPerformer { Performer = submissive }],
            },
            new Video
            {
                Id = 423,
                Title = "Solo appearance video",
                Date = date,
                VideoPerformers = [new VideoPerformer { Performer = solo }],
            });
        context.Audios.AddRange(
            new Audio
            {
                Id = 821,
                Title = "First appearance audio",
                AudioPerformers = [new AudioPerformer { Performer = dominant }, new AudioPerformer { Performer = submissive }],
            },
            new Audio
            {
                Id = 822,
                Title = "Second appearance audio",
                AudioPerformers = [new AudioPerformer { Performer = dominant }, new AudioPerformer { Performer = submissive }],
            });
        context.TagApplications.AddRange(
            Occurrence(AffinityHostType.Video, 421, DominantPerformerId, DominantTagId),
            Occurrence(AffinityHostType.Video, 421, SubmissivePerformerId, SubmissiveTagId),
            Occurrence(AffinityHostType.Video, 422, SubmissivePerformerId, DominantTagId),
            Occurrence(AffinityHostType.Video, 423, SoloPerformerId, DominantTagId),
            Occurrence(AffinityHostType.Audio, 821, DominantPerformerId, DominantTagId),
            Occurrence(AffinityHostType.Audio, 821, SubmissivePerformerId, DominantTagId),
            Occurrence(AffinityHostType.Audio, 822, DominantPerformerId, SubmissiveTagId),
            Occurrence(AffinityHostType.Audio, 822, SubmissivePerformerId, DominantTagId));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        static TagApplication Occurrence(AffinityHostType hostType, int hostId, int performerId, int tagId) => new()
        {
            HostType = hostType,
            HostId = hostId,
            ContextType = "performer",
            ContextId = performerId,
            TagId = tagId,
            SourceKey = "test",
        };
    }

    private static MultiIdCriterion Includes(int tagId) => new() { Modifier = CriterionModifier.Includes, Value = [tagId] };

    private static StringCriterion TitleIncludes(string value) => new() { Modifier = CriterionModifier.Includes, Value = value };

    private static Task<int[]> FindPerformerIdsAsync(
        EntityListSortBehaviorHarnessTests.SortHarnessFixture fixture,
        RelatedFilterCriterion<VideoFilter> criterion)
        => FindPerformerIdsAsync(fixture, new PerformerFilter { VideoFilterCriterion = criterion });

    private static Task<int[]> FindPerformerIdsAsync(
        EntityListSortBehaviorHarnessTests.SortHarnessFixture fixture,
        RelatedFilterCriterion<AudioFilter> criterion)
        => FindPerformerIdsAsync(fixture, new PerformerFilter { AudioFilterCriterion = criterion });

    private static async Task<int[]> FindPerformerIdsAsync(EntityListSortBehaviorHarnessTests.SortHarnessFixture fixture, PerformerFilter filter)
        => (await new PerformerRepository(fixture.Context).FindAsync(filter, DefaultFindFilter(), TestContext.Current.CancellationToken))
            .Items.Select(performer => performer.Id)
            .Order()
            .ToArray();

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
