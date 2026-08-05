using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public class VideoFilterBehaviorTests
{
    [Fact]
    public async Task PathCriterion_Equals_UsesFullNormalizedPath()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("match", folderPath: @"C:\library\matching", basename: "clip.mp4"),
            CreateVideoWithFile("same-name-other-folder", folderPath: @"C:\library\other", basename: "clip.mp4"));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching\clip.mp4",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["match"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DateCriterion_NullModifiers_ApplyWithoutAValue()
    {
        await using var context = CreateContext();
        var undated = CreateVideoWithFile("undated", basename: "undated.mp4");
        undated.Date = null;
        context.Videos.AddRange(CreateVideoWithFile("dated", videoDate: new DateOnly(2024, 5, 1)), undated);
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        // The filter UI sends an empty value for IS_NULL/NOT_NULL, so these must not
        // depend on Value parsing as a date.
        var (isNullItems, isNullCount) = await repository.FindAsync(
            new VideoFilter
            {
                DateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.IsNull },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (notNullItems, notNullCount) = await repository.FindAsync(
            new VideoFilter
            {
                DateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.NotNull },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, isNullCount);
        Assert.Equal(["undated"], isNullItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, notNullCount);
        Assert.Equal(["dated"], notNullItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerDateCriterion_NullModifiers_ApplyWithoutAValue()
    {
        await using var context = CreateContext();
        context.Performers.AddRange(
            new Performer { Name = "living", Birthdate = new DateOnly(1990, 3, 4) },
            new Performer { Name = "unknown-birthdate", Birthdate = null });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);

        var (isNullItems, isNullCount) = await repository.FindAsync(
            new PerformerFilter
            {
                BirthdateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.IsNull },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, isNullCount);
        Assert.Equal(["unknown-birthdate"], isNullItems.Select(performer => performer.Name).ToArray());
    }

    [Fact]
    public async Task AudioCodecCriterion_HandlesRegexAndNullModifiers()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("aac-video", audioCodec: "AAC"),
            CreateVideoWithFile("mp3-video", audioCodec: "MP3"),
            CreateVideoWithFile("missing-audio", audioCodec: ""));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        var (notRegexItems, notRegexCount) = await repository.FindAsync(
            new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = "^aa",
                    Modifier = CriterionModifier.NotMatchesRegex,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (nullItems, nullCount) = await repository.FindAsync(
            new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (notNullItems, notNullCount) = await repository.FindAsync(
            new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(2, notRegexCount);
        Assert.Equal(["missing-audio", "mp3-video"], notRegexItems.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray());
        Assert.Equal(1, nullCount);
        Assert.Equal(["missing-audio"], nullItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(2, notNullCount);
        Assert.Equal(["aac-video", "mp3-video"], notNullItems.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray());
    }

    [Fact]
    public async Task BitrateInterval_GreaterThan_UsesMaxBitRateSummary()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("high-bitrate", bitRate: 2_500_000),
            CreateVideoWithFile("low-bitrate", bitRate: 500_000),
            new Video { Title = "no-file" });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            BitrateInterval = new IntCriterion
            {
                Value = 1000,
                Modifier = CriterionModifier.GreaterThan,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["high-bitrate"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task BitrateSort_UsesMaxBitRateSummary()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("high-bitrate", bitRate: 2_500_000),
            CreateVideoWithFile("low-bitrate", bitRate: 500_000),
            CreateVideoWithFile("mid-bitrate", bitRate: 1_500_000));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sort = "bitrate",
            Direction = SortDirection.Asc,
        });

        Assert.Equal(3, totalCount);
        Assert.Equal(["low-bitrate", "mid-bitrate", "high-bitrate"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DirectorCriterion_NotMatchesRegex_UsesRegexSemantics()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("jane-video", director: "Jane Smith"),
            CreateVideoWithFile("john-video", director: "John Doe"));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            DirectorCriterion = new StringCriterion
            {
                Value = "^Jane",
                Modifier = CriterionModifier.NotMatchesRegex,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["john-video"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerAgeCriterion_Equals_UsesAgeAtVideoDate()
    {
        await using var context = CreateContext();
        var performer = CreatePerformer("Boundary Performer", new DateOnly(2006, 1, 15));

        context.Videos.AddRange(
            CreateVideoWithFile("before-birthday", videoDate: new DateOnly(2024, 1, 10), performer: performer),
            CreateVideoWithFile("after-birthday", videoDate: new DateOnly(2024, 1, 20), performer: performer));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformerAgeCriterion = new IntCriterion
            {
                Value = 18,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["after-birthday"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerTagsCriterion_Includes_MatchesVideosByPerformerOccurrenceTag()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Featured" };
        var taggedVideo = CreateVideoWithFile("tagged-performer-video", performer: CreatePerformer("Tagged", new DateOnly(2000, 1, 1)));
        var untaggedVideo = CreateVideoWithFile("untagged-performer-video", performer: CreatePerformer("Untagged", new DateOnly(2000, 1, 1)));

        context.Tags.Add(tag);
        context.Videos.AddRange(taggedVideo, untaggedVideo);
        await context.SaveChangesAsync();

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = taggedVideo.Id,
            ContextType = "performer",
            ContextId = taggedVideo.VideoPerformers.Single().Performer!.Id,
            TagId = tag.Id,
            SourceKey = "test",
        });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["tagged-performer-video"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetTaggedVideo = CreateVideoWithFile("target-tagged", performer: targetPerformer);
        var wrongPerformerTaggedVideo = CreateVideoWithFile("wrong-performer-tagged", performer: targetPerformer);
        wrongPerformerTaggedVideo.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Videos.AddRange(targetTaggedVideo, wrongPerformerTaggedVideo);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = targetTaggedVideo.Id,
                ContextType = "performer",
                ContextId = targetPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = wrongPerformerTaggedVideo.Id,
                ContextType = "performer",
                ContextId = otherPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                RequiredIds = [targetPerformer.Id],
                Modifier = CriterionModifier.Includes,
            },
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["target-tagged"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData(AffinityHostType.Image, "image")]
    [InlineData(AffinityHostType.Audio, "audio")]
    [InlineData(AffinityHostType.Text, "text")]
    public async Task TagApplicationService_AddAsync_AllowsPerformerContextForMediaHosts(AffinityHostType hostType, string hostTypeValue)
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = $"{hostTypeValue}-occurrence" };
        var performer = CreatePerformer($"{hostTypeValue}-performer", new DateOnly(2000, 1, 1));

        var hostId = hostType switch
        {
            AffinityHostType.Image => AddImageHost(context, CreateImage($"{hostTypeValue}-host", performer)),
            AffinityHostType.Audio => AddAudioHost(context, CreateAudio($"{hostTypeValue}-host", performer)),
            AffinityHostType.Text => AddTextHost(context, CreateTextDocument($"{hostTypeValue}-host", performer)),
            _ => throw new ArgumentOutOfRangeException(nameof(hostType), hostType, null),
        };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();

        var service = new TagApplicationService(context);
        var application = await service.AddAsync(
            new TagApplicationCreateDto(hostTypeValue, hostId, tag.Id, "user", "performer", performer.Id),
            CancellationToken.None);

        Assert.Equal(hostType, application.HostType);
        Assert.Equal(hostId, application.HostId);
        Assert.Equal("performer", application.ContextType);
        Assert.Equal(performer.Id, application.ContextId);
        Assert.Equal(tag.Id, application.TagId);
    }

    [Fact]
    public async Task ImageFilter_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Image Occurrence Tag" };
        var targetPerformer = CreatePerformer("Image Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Image Other", new DateOnly(2000, 1, 1));
        var targetTaggedImage = CreateImage("target-tagged-image", targetPerformer);
        var wrongPerformerTaggedImage = CreateImage("wrong-performer-tagged-image", targetPerformer);
        wrongPerformerTaggedImage.ImagePerformers.Add(new ImagePerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Images.AddRange(targetTaggedImage, wrongPerformerTaggedImage);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, targetTaggedImage.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, wrongPerformerTaggedImage.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync();

        var repository = new ImageRepository(context);
        var filter = new ImageFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                RequiredIds = [targetPerformer.Id],
                Modifier = CriterionModifier.Includes,
            },
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["target-tagged-image"], items.Select(image => image.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AudiosController_FindPost_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Audio Occurrence Tag" };
        var targetPerformer = CreatePerformer("Audio Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Audio Other", new DateOnly(2000, 1, 1));
        var targetTaggedAudio = CreateAudio("target-tagged-audio", targetPerformer);
        var wrongPerformerTaggedAudio = CreateAudio("wrong-performer-tagged-audio", targetPerformer);
        wrongPerformerTaggedAudio.AudioPerformers.Add(new AudioPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Audios.AddRange(targetTaggedAudio, wrongPerformerTaggedAudio);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, targetTaggedAudio.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, wrongPerformerTaggedAudio.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync();

        var controller = new AudiosController(context, new CustomFieldService(context), null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter
            {
                PerformersCriterion = new MultiIdCriterion { RequiredIds = [targetPerformer.Id], Modifier = CriterionModifier.Includes },
                PerformerTagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<AudioDto>>(ok.Value);
        var audio = Assert.Single(payload.Items);

        Assert.Equal("target-tagged-audio", audio.Title);
    }

    [Fact]
    public async Task TextsController_FindPost_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Text Occurrence Tag" };
        var targetPerformer = CreatePerformer("Text Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Text Other", new DateOnly(2000, 1, 1));
        var targetTaggedText = CreateTextDocument("target-tagged-text", targetPerformer);
        var wrongPerformerTaggedText = CreateTextDocument("wrong-performer-tagged-text", targetPerformer);
        wrongPerformerTaggedText.TextPerformers.Add(new TextPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.TextDocuments.AddRange(targetTaggedText, wrongPerformerTaggedText);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, targetTaggedText.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, wrongPerformerTaggedText.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync();

        var controller = new TextsController(context, new CustomFieldService(context), null!, null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<TextDocumentFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new TextDocumentFilter
            {
                PerformersCriterion = new MultiIdCriterion { RequiredIds = [targetPerformer.Id], Modifier = CriterionModifier.Includes },
                PerformerTagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<TextDocumentDto>>(ok.Value);
        var text = Assert.Single(payload.Items);

        Assert.Equal("target-tagged-text", text.Title);
    }

    [Fact]
    public async Task TagDurationCriterion_AppliesAllClauses()
    {
        await using var context = CreateContext();
        var shortTag = new Tag { Name = "Short" };
        var percentTag = new Tag { Name = "Percent" };
        var matchingVideo = CreateVideoWithFile("matching-duration");
        var longVideo = CreateVideoWithFile("too-long");
        var lowPercentVideo = CreateVideoWithFile("low-percent");

        context.Tags.AddRange(shortTag, percentTag);
        context.Videos.AddRange(matchingVideo, longVideo, lowPercentVideo);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreateDurationApplication(matchingVideo.Id, shortTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(matchingVideo.Id, percentTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(longVideo.Id, shortTag.Id, totalDurationSec: 40, hostDurationSec: 100),
            CreateDurationApplication(longVideo.Id, percentTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(lowPercentVideo.Id, shortTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(lowPercentVideo.Id, percentTag.Id, totalDurationSec: 5, hostDurationSec: 100));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagDurationCriterion = new TagDurationCriterion
            {
                Clauses =
                [
                    new TagDurationClause { TagId = shortTag.Id, Modifier = CriterionModifier.LessThan, Unit = "seconds", Value = 30 },
                    new TagDurationClause { TagId = percentTag.Id, Modifier = CriterionModifier.GreaterThan, Unit = "percent", Value = 10 },
                ],
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["matching-duration"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AggregateAsync_SumsOnlyVideosMatchingTheActiveFilter()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            new Video { Title = "included", Organized = true, MaxDuration = 90.5, MaxFileSize = 1_500 },
            new Video { Title = "also included", Organized = true, MaxDuration = 29.5, MaxFileSize = 2_500 },
            new Video { Title = "excluded", Organized = false, MaxDuration = 600, MaxFileSize = 50_000 });
        await context.SaveChangesAsync();

        var aggregate = await new VideoRepository(context).AggregateAsync(
            new VideoFilter { Organized = true },
            new FindFilter());

        Assert.Equal(2, aggregate.Count);
        Assert.Equal(120, aggregate.Duration);
        Assert.Equal(4_000, aggregate.FileSize);
    }

    [Fact]
    public async Task MediaAggregates_SumFilteredImageAudioAndTextMetrics()
    {
        await using var context = CreateContext();
        var image = new Image { Title = "image", MaxFileSize = 4_000 };
        image.Files.Add(new ImageFile { Path = "/media/image.jpg", Basename = "image.jpg", Width = 2_000, Height = 1_000, Size = 4_000 });
        context.Images.AddRange(image, new Image { Title = "excluded image", MaxFileSize = 90_000 });
        var audio = new Audio { Title = "audio", MaxDuration = 75.5, MaxFileSize = 5_000 };
        var excludedAudio = new Audio { Title = "excluded audio", MaxDuration = 900, MaxFileSize = 90_000 };
        var text = new TextDocument { Title = "text", MaxFileSize = 6_000 };
        var excludedText = new TextDocument { Title = "excluded text", MaxFileSize = 90_000 };
        context.Audios.AddRange(audio, excludedAudio);
        context.TextDocuments.AddRange(text, excludedText);
        await context.SaveChangesAsync();

        var imageAggregate = await new ImageRepository(context).AggregateAsync(
            new ImageFilter { Ids = [image.Id] }, new FindFilter());
        Assert.Equal(1, imageAggregate.Count);
        Assert.Equal(4_000, imageAggregate.FileSize);

        var audioResponse = await new AudiosController(context, null!, null!, null!, null!, null).Aggregate(
            new FilteredQueryRequest<AudioFilter> { Ids = [audio.Id] }, CancellationToken.None);
        var audioAggregate = Assert.IsType<AudioAggregate>(Assert.IsType<OkObjectResult>(audioResponse.Result).Value);
        Assert.Equal(75.5, audioAggregate.Duration);
        Assert.Equal(5_000, audioAggregate.FileSize);

        var textResponse = await new TextsController(context, null!, null!, null!, null!, null!, null).Aggregate(
            new FilteredQueryRequest<TextDocumentFilter> { Ids = [text.Id] }, CancellationToken.None);
        var textAggregate = Assert.IsType<TextAggregate>(Assert.IsType<OkObjectResult>(textResponse.Result).Value);
        Assert.Equal(6_000, textAggregate.FileSize);
    }

    [Fact]
    public async Task TagsCriterion_UsesThresholdQualifiedDerivedTagApplications()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Action", MinOccurrencePercent = 80 };
        var qualifyingVideo = CreateVideoWithFile("qualifying-derived");
        var belowThresholdVideo = CreateVideoWithFile("below-threshold-derived");
        var manualVideo = CreateVideoWithFile("manual-tagged");
        manualVideo.VideoTags.Add(new VideoTag { Tag = tag });

        context.Tags.Add(tag);
        context.Videos.AddRange(qualifyingVideo, belowThresholdVideo, manualVideo);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreateDurationApplication(qualifyingVideo.Id, tag.Id, totalDurationSec: 82, hostDurationSec: 100),
            CreateDurationApplication(belowThresholdVideo.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100),
            CreateDurationApplication(manualVideo.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var binaryFilter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
        };
        var explicitDurationFilter = new VideoFilter
        {
            TagDurationCriterion = new TagDurationCriterion
            {
                TagId = tag.Id,
                Unit = "percent",
                Modifier = CriterionModifier.GreaterThan,
                Value = 70,
            },
        };

        var (binaryItems, binaryCount) = await repository.FindAsync(binaryFilter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" });
        var (durationItems, durationCount) = await repository.FindAsync(explicitDurationFilter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        Assert.Equal(2, binaryCount);
        Assert.Equal(["manual-tagged", "qualifying-derived"], binaryItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(3, durationCount);
        Assert.Contains(durationItems, video => video.Title == "below-threshold-derived");
    }

    [Fact]
    public async Task TagsCriterion_WhenSecondsOrPercentThresholdMatches_TreatsDerivedTagAsEffective()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Running", MinOccurrenceSec = 30, MinOccurrencePercent = 80 };
        var secondsVideo = CreateVideoWithFile("seconds-match");
        var percentVideo = CreateVideoWithFile("percent-match");
        var neitherVideo = CreateVideoWithFile("neither-match");

        context.Tags.Add(tag);
        context.Videos.AddRange(secondsVideo, percentVideo, neitherVideo);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            CreateDurationApplication(secondsVideo.Id, tag.Id, totalDurationSec: 35, hostDurationSec: 100),
            CreateDurationApplication(percentVideo.Id, tag.Id, totalDurationSec: 8, hostDurationSec: 10),
            CreateDurationApplication(neitherVideo.Id, tag.Id, totalDurationSec: 20, hostDurationSec: 100));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        Assert.Equal(2, totalCount);
        Assert.Equal(["percent-match", "seconds-match"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task VideosController_GetById_MapsOnlyEffectiveDerivedTagsAsNonRemovable()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Observed", MinOccurrencePercent = 80 };
        var video = CreateVideoWithFile("thresholded-video");

        context.Tags.Add(tag);
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.TagApplications.Add(CreateDurationApplication(video.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100));
        await context.SaveChangesAsync();

        var controller = CreateVideosControllerWithRepository(context);
        var initialResponse = await controller.GetById(video.Id, CancellationToken.None);
        var initialOk = Assert.IsType<OkObjectResult>(initialResponse.Result);
        var initialVideo = Assert.IsType<VideoDto>(initialOk.Value);
        Assert.Empty(initialVideo.Tags);

        tag.MinOccurrencePercent = 70;
        await context.SaveChangesAsync();

        var updatedResponse = await controller.GetById(video.Id, CancellationToken.None);
        var updatedOk = Assert.IsType<OkObjectResult>(updatedResponse.Result);
        var updatedVideo = Assert.IsType<VideoDto>(updatedOk.Value);
        var effectiveTag = Assert.Single(updatedVideo.Tags);

        Assert.Equal(tag.Id, effectiveTag.Id);
        Assert.True(effectiveTag.IsDerived);
        Assert.False(effectiveTag.CanRemove);
        Assert.Equal(72, effectiveTag.EffectiveDurationPercent.GetValueOrDefault(), 3);
    }

    [Fact]
    public async Task VideosController_GetById_LocksDirectAiOnlyTagsAsNonRemovable()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "AI Link" };
        var video = CreateVideoWithFile("ai-link-video");
        video.VideoTags.Add(new VideoTag { Tag = tag });

        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            TagId = tag.Id,
            SourceKey = "ext:ai.tagging",
            SourceRunId = "run-ai-link",
            ModelKey = "tagger-v1",
            Confidence = 0.9f,
        });
        await context.SaveChangesAsync();

        var controller = CreateVideosControllerWithRepository(context);
        var response = await controller.GetById(video.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<VideoDto>(ok.Value);
        var effectiveTag = Assert.Single(dto.Tags);

        Assert.Equal(tag.Id, effectiveTag.Id);
        Assert.True(effectiveTag.IsDerived);
        Assert.False(effectiveTag.CanRemove);
    }

    [Fact]
    public async Task TagVideoCount_RefreshesFromEffectiveDerivedTagsWhenThresholdChanges()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Counted", MinOccurrencePercent = 80 };
        var video = CreateVideoWithFile("counted-video");

        context.Tags.Add(tag);
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.TagApplications.Add(CreateDurationApplication(video.Id, tag.Id, totalDurationSec: 82, hostDurationSec: 100));
        await context.SaveChangesAsync();

        await context.Entry(tag).ReloadAsync();
        Assert.Equal(1, tag.VideoCount);

        tag.MinOccurrencePercent = 90;
        await context.SaveChangesAsync();
        await context.Entry(tag).ReloadAsync();

        Assert.Equal(0, tag.VideoCount);
    }

    [Fact]
    public async Task AudiosController_FindPost_UsesEffectiveDerivedTagsForAudioFiltersAndDtos()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Audio Cue", MinOccurrenceSec = 5 };
        var matchingAudio = new Audio { Title = "matching-audio" };
        var belowThresholdAudio = new Audio { Title = "below-threshold-audio" };

        context.Tags.Add(tag);
        context.Audios.AddRange(matchingAudio, belowThresholdAudio);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = matchingAudio.Id,
                TagId = tag.Id,
                TotalDurationSec = 6,
                HostDurationSec = 60,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = belowThresholdAudio.Id,
                TagId = tag.Id,
                TotalDurationSec = 4,
                HostDurationSec = 60,
                SourceKey = "test",
            });
        await context.SaveChangesAsync();

        var controller = new AudiosController(context, new CustomFieldService(context), null!, null!, null!);
        var response = await controller.FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter
            {
                TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<AudioDto>>(ok.Value);
        var audio = Assert.Single(payload.Items);
        var effectiveTag = Assert.Single(audio.Tags);

        Assert.Equal("matching-audio", audio.Title);
        Assert.True(effectiveTag.IsDerived);
        Assert.False(effectiveTag.CanRemove);
        Assert.Equal(6, effectiveTag.EffectiveDurationSec.GetValueOrDefault(), 3);
    }

    [Fact]
    public async Task HashAndChecksumCriteria_FilterVideoFingerprints()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile(
                "matching-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                ]),
            CreateVideoWithFile(
                "other-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                ]));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        var (hashItems, hashCount) = await repository.FindAsync(
            new VideoFilter
            {
                HashCriterion = new StringCriterion
                {
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (checksumItems, checksumCount) = await repository.FindAsync(
            new VideoFilter
            {
                ChecksumCriterion = new StringCriterion
                {
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, hashCount);
        Assert.Equal(["matching-hashes"], hashItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, checksumCount);
        Assert.Equal(["matching-hashes"], checksumItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task FingerprintCriterion_FiltersVideosBySelectedAlgorithm()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile(
                "matching-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                    new FileFingerprint { Type = "phash", Value = "phash-match" },
                ]),
            CreateVideoWithFile(
                "other-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                    new FileFingerprint { Type = "phash", Value = "phash-other" },
                ]));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        var (oshashItems, oshashCount) = await repository.FindAsync(
            new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "oshash",
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (md5Items, md5Count) = await repository.FindAsync(
            new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "md5",
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (phashItems, phashCount) = await repository.FindAsync(
            new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "phash",
                    Value = "phash-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, oshashCount);
        Assert.Equal(["matching-fingerprint-types"], oshashItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, md5Count);
        Assert.Equal(["matching-fingerprint-types"], md5Items.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, phashCount);
        Assert.Equal(["matching-fingerprint-types"], phashItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task HasSegmentsCriterion_FiltersVideosByRawSegmentPresence()
    {
        await using var context = CreateContext();
        var withSegments = CreateVideoWithFile("with-segments");
        var withoutSegments = CreateVideoWithFile("without-segments");
        context.Videos.AddRange(withSegments, withoutSegments);
        await context.SaveChangesAsync();

        context.Segments.AddRange(
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = withSegments.Id,
                StartSec = 1,
                EndSec = 2,
                SourceKey = "user",
            },
            new Segment
            {
                HostType = SegmentHostType.Image,
                HostId = withoutSegments.Id,
                StartSec = 1,
                EndSec = 2,
                SourceKey = "user",
            });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        var (withSegmentItems, withSegmentCount) = await repository.FindAsync(
            new VideoFilter { HasSegmentsCriterion = new BoolCriterion { Value = true } },
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        var (withoutSegmentItems, withoutSegmentCount) = await repository.FindAsync(
            new VideoFilter { HasSegmentsCriterion = new BoolCriterion { Value = false } },
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        Assert.Equal(1, withSegmentCount);
        Assert.Equal(["with-segments"], withSegmentItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, withoutSegmentCount);
        Assert.Equal(["without-segments"], withoutSegmentItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DuplicatedPhashCriterion_True_FindsVideosSharingAPhashAcrossVideos()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("duplicate-a", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateVideoWithFile("duplicate-b", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateVideoWithFile("unique", fingerprints: [new FileFingerprint { Type = "phash", Value = "unique-phash" }]));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            DuplicatedPhashCriterion = new BoolCriterion { Value = true },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        Assert.Equal(2, totalCount);
        Assert.Equal(["duplicate-a", "duplicate-b"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task VideosController_Find_BindsSeedFromQuery()
    {
        var repository = new CapturingVideoRepository();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext();
        var controller = new VideosController(repository, context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());

        await controller.Find(q: null, page: 1, perPage: 25, sort: "random", direction: "desc", seed: 12345, ct: default);

        Assert.Equal(12345, repository.LastFindFilter?.Seed);
        Assert.Equal("random", repository.LastFindFilter?.Sort);
        Assert.Equal(Cove.Core.Enums.SortDirection.Desc, repository.LastFindFilter?.Direction);
    }

    [Fact]
    public async Task VideosController_Find_BindsOrderedSortClausesFromQuery()
    {
        var repository = new CapturingVideoRepository();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        await using var context = CreateContext();
        var controller = new VideosController(repository, context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());

        await controller.Find(q: null, page: 1, perPage: 25, sort: null, direction: null, seed: null, sorts: "studio:asc,date:desc", ct: default);

        Assert.Equal(
            [
                new SortClause("studio", Cove.Core.Enums.SortDirection.Asc),
                new SortClause("date", Cove.Core.Enums.SortDirection.Desc),
            ],
            repository.LastFindFilter?.Sorts);
    }

    [Fact]
    public async Task VideoRepository_SortsByStudioThenDate()
    {
        await using var context = CreateContext();
        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        var alphaOld = CreateVideoWithFile("alpha-old");
        alphaOld.Studio = alphaStudio;
        alphaOld.Date = new DateOnly(2024, 1, 1);
        var alphaNew = CreateVideoWithFile("alpha-new");
        alphaNew.Studio = alphaStudio;
        alphaNew.Date = new DateOnly(2024, 2, 1);
        var betaOld = CreateVideoWithFile("beta-old");
        betaOld.Studio = betaStudio;
        betaOld.Date = new DateOnly(2024, 1, 1);
        var betaNew = CreateVideoWithFile("beta-new");
        betaNew.Studio = betaStudio;
        betaNew.Date = new DateOnly(2024, 2, 1);

        context.Videos.AddRange(betaOld, alphaOld, betaNew, alphaNew);
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter
        {
            Page = 1,
            PerPage = 20,
            Sorts =
            [
                new SortClause("studio", Cove.Core.Enums.SortDirection.Asc),
                null!,
                new SortClause("date", Cove.Core.Enums.SortDirection.Desc),
            ],
        });

        Assert.Equal(4, totalCount);
        Assert.Equal(
            ["alpha-new", "alpha-old", "beta-new", "beta-old"],
            items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task VideosController_FindWithCompilations_ReturnsVideoRangeGroupsAsPagedRows()
    {
        await using var context = CreateContext();
        var video = CreateVideoWithFile("video row");
        video.CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        video.UpdatedAt = video.CreatedAt;
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.Groups.AddRange(
            new Group
            {
                Name = "compilation row",
                CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = true,
                GroupItems = [new GroupItem { Kind = GroupItemKind.VideoRange, VideoId = video.Id, HostId = video.Id, StartSec = 10, EndSec = 20 }],
            },
            new Group
            {
                Name = "ordinary video group",
                CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = true,
                GroupItems = [new GroupItem { Kind = GroupItemKind.Video, VideoId = video.Id, HostId = video.Id }],
            },
            new Group
            {
                Name = "hidden compilation",
                CreatedAt = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = false,
                GroupItems = [new GroupItem { Kind = GroupItemKind.VideoRange, VideoId = video.Id, HostId = video.Id, StartSec = 20, EndSec = 30 }],
            });
        await context.SaveChangesAsync();

        var controller = CreateVideosController(context);

        var response = await controller.FindWithCompilations(
            q: null, page: 1, perPage: 10, sort: "created_at", direction: "desc", seed: null,
            title: null, rating: null, organized: null, studioId: null, groupId: null, galleryId: null,
            tagIds: null, performerIds: null, ct: default);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<VideoListEntryDto>>(ok.Value);

        Assert.Equal(3, payload.TotalCount);
        Assert.Equal(["compilation", "compilation", "video"], payload.Items.Select(item => item.Kind).ToArray());
        Assert.Equal("ordinary video group", payload.Items[0].Group?.Name);
        Assert.True(payload.Items[0].Group?.IsCompilation);
        Assert.Equal("compilation row", payload.Items[1].Group?.Name);
        Assert.True(payload.Items[1].Group?.IsCompilation);
        Assert.Equal("video row", payload.Items[2].Video?.Title);
    }

    [Fact]
    public async Task VideosController_FindDuplicates_ExactFingerprint_UsesMd5AndOshash()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("md5 duplicate a", basename: "a.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "same-md5" }]),
            CreateVideoWithFile("md5 duplicate b", basename: "b.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "same-md5" }]),
            CreateVideoWithFile("oshash duplicate a", basename: "c.mp4", fingerprints: [new FileFingerprint { Type = "oshash", Value = "same-oshash" }]),
            CreateVideoWithFile("oshash duplicate b", basename: "d.mp4", fingerprints: [new FileFingerprint { Type = "oshash", Value = "same-oshash" }]),
            CreateVideoWithFile("unique", basename: "e.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "unique-md5" }]));
        await context.SaveChangesAsync();

        var controller = CreateVideosController(context);

        var response = await controller.FindDuplicates(matchType: "fingerprint", ct: default);

        var groups = GetDuplicateGroups(response);
        Assert.Contains(groups, group => group.Select(video => video.Title ?? "").OrderBy(title => title).SequenceEqual(["md5 duplicate a", "md5 duplicate b"]));
        Assert.Contains(groups, group => group.Select(video => video.Title ?? "").OrderBy(title => title).SequenceEqual(["oshash duplicate a", "oshash duplicate b"]));
        Assert.DoesNotContain(groups.SelectMany(group => group), video => video.Title == "unique");
    }

    [Fact]
    public async Task VideosController_FindDuplicates_Phash_UsesDistanceAndDurationTolerance()
    {
        await using var context = CreateContext();
        context.Videos.AddRange(
            CreateVideoWithFile("visual duplicate a", basename: "a.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "0000000000000000" }]),
            CreateVideoWithFile("visual duplicate b", basename: "b.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "0000000000000001" }]),
            CreateVideoWithFile("different visual", basename: "c.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "ffffffffffffffff" }]));
        await context.SaveChangesAsync();

        var controller = CreateVideosController(context);

        var response = await controller.FindDuplicates(matchType: "phash", distance: 1, durationDiff: 0, ct: default);

        var groups = GetDuplicateGroups(response);
        var group = Assert.Single(groups);
        Assert.Equal(["visual duplicate a", "visual duplicate b"], group.Select(video => video.Title ?? "").OrderBy(title => title).ToArray());
    }

    [Fact]
    public async Task LastPlayedAtSort_Descending_PutsPlayedVideosBeforeUnplayedVideos()
    {
        await using var context = CreateContext();
        var neverPlayed = new Video { Title = "never-played" };
        var olderPlay = new Video { Title = "older-play" };
        var recentPlay = new Video { Title = "recent-play" };
        context.Videos.AddRange(neverPlayed, olderPlay, recentPlay);
        await context.SaveChangesAsync();

        context.UserEntityAffinities.AddRange(
            new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = olderPlay.Id, LastConsumedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc) },
            new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = recentPlay.Id, LastConsumedAt = new DateTime(2024, 1, 12, 8, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);

        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter
            {
                Page = 1,
                PerPage = 50,
                Sort = "last_played_at",
                Direction = Cove.Core.Enums.SortDirection.Desc,
            });

        Assert.Equal(3, totalCount);
        Assert.Equal(["recent-play", "older-play", "never-played"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    private static Video CreateVideoWithFile(
        string title,
        string? director = null,
        DateOnly? videoDate = null,
        string folderPath = @"C:\library",
        string basename = "clip.mp4",
        string audioCodec = "AAC",
        string videoCodec = "H264",
        long bitRate = 1_000_000,
        Performer? performer = null,
        IEnumerable<FileFingerprint>? fingerprints = null)
    {
        var video = new Video
        {
            Title = title,
            Director = director,
            Date = videoDate ?? new DateOnly(2024, 1, 1),
        };

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            AudioCodec = audioCodec,
            VideoCodec = videoCodec,
            BitRate = bitRate,
            FrameRate = 30,
            Duration = 120,
            Width = 1920,
            Height = 1080,
            Format = "mp4",
            Size = 1024,
            ModTime = DateTime.UtcNow,
        };

        if (fingerprints != null)
        {
            foreach (var fingerprint in fingerprints)
            {
                file.Fingerprints.Add(fingerprint);
            }
        }

        video.Files.Add(file);

        if (performer != null)
        {
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        }

        return video;
    }

    private static Performer CreatePerformer(string name, DateOnly birthdate, params Tag[] tags)
    {
        var performer = new Performer
        {
            Name = name,
            Birthdate = birthdate,
        };

        foreach (var tag in tags)
        {
            performer.PerformerTags.Add(new PerformerTag { Performer = performer, Tag = tag });
        }

        return performer;
    }

    private static TagApplication CreateDurationApplication(int videoId, int tagId, double totalDurationSec, double hostDurationSec)
        => new()
        {
            HostType = AffinityHostType.Video,
            HostId = videoId,
            TagId = tagId,
            TotalDurationSec = totalDurationSec,
            HostDurationSec = hostDurationSec,
            SourceKey = "test",
        };

    private static TagApplication CreatePerformerOccurrenceApplication(AffinityHostType hostType, int hostId, int performerId, int tagId)
        => new()
        {
            HostType = hostType,
            HostId = hostId,
            ContextType = "performer",
            ContextId = performerId,
            TagId = tagId,
            SourceKey = "test",
        };

    private static Image CreateImage(string title, Performer? performer = null)
    {
        var image = new Image
        {
            Title = title,
        };

        if (performer != null)
        {
            image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        }

        return image;
    }

    private static Audio CreateAudio(string title, Performer? performer = null)
    {
        var audio = new Audio
        {
            Title = title,
        };

        if (performer != null)
        {
            audio.AudioPerformers.Add(new AudioPerformer { Performer = performer });
        }

        return audio;
    }

    private static TextDocument CreateTextDocument(string title, Performer? performer = null)
    {
        var textDocument = new TextDocument
        {
            Title = title,
        };

        if (performer != null)
        {
            textDocument.TextPerformers.Add(new TextPerformer { Performer = performer });
        }

        return textDocument;
    }

    private static int AddImageHost(CoveContext context, Image image)
    {
        context.Images.Add(image);
        return image.Id;
    }

    private static int AddAudioHost(CoveContext context, Audio audio)
    {
        context.Audios.Add(audio);
        return audio.Id;
    }

    private static int AddTextHost(CoveContext context, TextDocument textDocument)
    {
        context.TextDocuments.Add(textDocument);
        return textDocument.Id;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"video-filter-behavior-{Guid.NewGuid():N}")
            .Options;

        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });

        return new TestCoveContext(options, principalAccessor);
    }

    private static VideosController CreateVideosController(CoveContext context)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new VideosController(new CapturingVideoRepository(), context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());
    }

    private static VideosController CreateVideosControllerWithRepository(CoveContext context)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new VideosController(new VideoRepository(context), context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());
    }

    private static List<List<VideoDto>> GetDuplicateGroups(ActionResult<List<List<VideoDto>>> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<List<List<VideoDto>>>(ok.Value);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class CapturingVideoRepository : IVideoRepository
    {
        public FindFilter? LastFindFilter { get; private set; }

        public Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
        {
            LastFindFilter = findFilter;
            return Task.FromResult<(IReadOnlyList<Video>, int)>((Array.Empty<Video>(), 0));
        }

        public Task<VideoAggregate> AggregateAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
            => Task.FromResult(new VideoAggregate(0, 0, 0));

        public Task<Video?> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Video>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Video> AddAsync(Video entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Video entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<VideoPerformer>> GetVideoPerformersAsync(IReadOnlyList<int> videoIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Video?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
