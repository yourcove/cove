using System.Data.Common;
using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

public class DynamicGroupsAndBookmarksTests
{
    [Fact]
    public async Task BookmarkToggleAndBatch_AreUserScoped()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Videos.Add(new Video { Title = "Saved Video" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var controller = new BookmarksController(context, principalAccessor, new NoOpUserEngagementService());

        principalAccessor.Set(CreatePrincipal(7));
        var saveResult = await controller.Toggle(new BookmarkToggleDto(AffinityHostType.Video, videoId, true), CancellationToken.None);
        var saveOk = Assert.IsType<OkObjectResult>(saveResult.Result);
        var saveState = Assert.IsType<BookmarkStateDto>(saveOk.Value);
        Assert.True(saveState.Saved);

        var userBatchResult = await controller.Batch(new BookmarkBatchRequestDto(AffinityHostType.Video, [videoId]), CancellationToken.None);
        var userBatchOk = Assert.IsType<OkObjectResult>(userBatchResult.Result);
        var userStates = Assert.IsAssignableFrom<IReadOnlyList<BookmarkStateDto>>(userBatchOk.Value);
        Assert.True(userStates.Single().Saved);

        context.ChangeTracker.Clear();
        principalAccessor.Set(CreatePrincipal(9));
        var otherBatchResult = await controller.Batch(new BookmarkBatchRequestDto(AffinityHostType.Video, [videoId]), CancellationToken.None);
        var otherBatchOk = Assert.IsType<OkObjectResult>(otherBatchResult.Result);
        var otherStates = Assert.IsAssignableFrom<IReadOnlyList<BookmarkStateDto>>(otherBatchOk.Value);
        Assert.False(otherStates.Single().Saved);

        Assert.Equal(1, await context.UserBookmarks.IgnoreQueryFilters().CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveForLaterDynamicGroup_ResolvesBookmarkedItemsNewestFirst()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var firstVideo = new Video { Title = "First" };
        var secondVideo = new Video { Title = "Second" };
        var group = new Group { Name = "Save for Later", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.SaveForLaterSourceKey };
        context.AddRange(firstVideo, secondVideo, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserBookmarks.AddRange(
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = firstVideo.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-10) },
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = secondVideo.Id, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        var items = await resolver.ResolveDtosAsync(group.Id, forceRefresh: true, CancellationToken.None);

        Assert.Equal(["Second", "First"], items.Select(item => item.Title ?? string.Empty).ToArray());
        Assert.All(items, item => Assert.Equal("video", item.HostType));
        Assert.Equal(2, await context.Groups.Where(item => item.Id == group.Id).Select(item => item.CachedItemCount).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveForLaterDynamicGroup_TotalCountExcludesMissingHydratedEntities()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var video = new Video { Title = "Still exists" };
        var group = new Group { Name = "Save for Later", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.SaveForLaterSourceKey };
        context.AddRange(video, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserBookmarks.AddRange(
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = video.Id, CreatedAt = DateTime.UtcNow },
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = 999_999, CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(video.Id, item.VideoId);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, await context.Groups.Where(item => item.Id == group.Id).Select(item => item.CachedItemCount).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContinueWatchingDynamicGroup_ExcludesCompletedVideos()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var unfinished = new Video { Title = "Unfinished", MaxDuration = 100 };
        var complete = new Video { Title = "Complete", MaxDuration = 100 };
        var group = new Group { Name = "Continue Watching", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.ContinueWatchingSourceKey };
        context.AddRange(unfinished, complete, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserEntityAffinities.AddRange(
            new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Video, HostId = unfinished.Id, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 42, TotalConsumedSec = 42 },
            new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Video, HostId = complete.Id, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 98, TotalConsumedSec = 96 },
            new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Video, HostId = 999_999, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 20, TotalConsumedSec = 20 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("Unfinished", item.Title);
        Assert.Equal(unfinished.Id, item.VideoId);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ContinueWatchingDynamicGroup_AppliesRequestedPageInDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(CreatePrincipal(7));
        var commands = new CommandRecorderInterceptor();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .AddInterceptors(commands)
            .Options;
        await using var context = new DynamicGroupTestContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.Users.Add(new User { Id = 7, Username = "user-7", PasswordHash = "test" });

        var now = DateTime.UtcNow;
        var videos = Enumerable.Range(1, 20)
            .Select(index => new Video { Title = $"Video {index}", MaxDuration = 100 })
            .ToList();
        var audio = new Audio { Title = "Audio item" };
        var group = new Group { Name = "Continue Watching", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.ContinueWatchingSourceKey };
        context.AddRange(videos);
        context.Add(audio);
        context.Add(group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserEntityAffinities.AddRange(videos.Select((video, index) => new UserEntityAffinity
        {
            UserId = 7,
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            LastConsumedAt = now.AddMinutes(-index),
            LastPositionSec = 25,
            TotalConsumedSec = 25,
            CompleteCount = index == 0 ? 1 : 0,
        }));
        context.UserEntityAffinities.AddRange(
            new UserEntityAffinity
            {
                UserId = 7,
                HostType = AffinityHostType.Audio,
                HostId = audio.Id,
                LastConsumedAt = now.AddMinutes(2),
                LastPositionSec = 25,
                TotalConsumedSec = 25,
            },
            new UserEntityAffinity
            {
                UserId = 7,
                HostType = AffinityHostType.Video,
                HostId = 999_999,
                LastConsumedAt = now.AddMinutes(3),
                LastPositionSec = 25,
                TotalConsumedSec = 25,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        commands.Clear();

        var source = new ContinueWatchingDynamicGroupSource(context);
        var result = await source.ResolveAsync(group, new DynamicGroupResolveContext(7, Offset: 4, Limit: 3), CancellationToken.None);

        Assert.Equal(20, result.TotalCount);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(["Video 5", "Video 6", "Video 7"], result.Items.Select(item => item.Title ?? string.Empty).ToArray());
        var affinityPageCommand = Assert.Single(commands.Commands, command =>
            command.Contains("FROM \"user_entity_affinities\"", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LIMIT", affinityPageCommand, StringComparison.OrdinalIgnoreCase);

        var resolver = CreateResolver(context, principalAccessor);
        var allItems = await resolver.ResolveDtosAsync(group.Id, forceRefresh: true, CancellationToken.None);
        Assert.Equal(20, allItems.Count);
        Assert.Equal("audio", allItems[0].HostType);
    }

    [Fact]
    public async Task ContinueWatchingDynamicGroup_IncludesAudioAndSegments()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var audio = new Audio { Title = "Unfinished audio" };
        var video = new Video { Title = "Segment video" };
        var group = new Group { Name = "Continue Watching", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.ContinueWatchingSourceKey };
        context.AddRange(audio, video, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            SourceKey = "test",
            StartSec = 12,
            EndSec = 24,
            Title = "Unfinished segment",
        };
        context.Segments.Add(segment);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserEntityAffinities.AddRange(
            new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Audio, HostId = audio.Id, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 33, TotalConsumedSec = 33 },
            new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Segment, HostId = segment.Id, LastConsumedAt = DateTime.UtcNow.AddMinutes(-1), LastPositionSec = 6, TotalConsumedSec = 6 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        var items = await resolver.ResolveDtosAsync(group.Id, forceRefresh: true, CancellationToken.None);

        Assert.Contains(items, item => item.HostType == "audio" && item.HostId == audio.Id && item.Title == "Unfinished audio");
        Assert.Contains(items, item => item.HostType == "segment" && item.HostId == segment.Id && item.VideoId == video.Id && item.StartSec == 12);
    }

    [Fact]
    public async Task DeletingEntity_RemovesEngagementRowsAndBookmarks()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var audio = new Audio { Title = "Delete me" };
        context.Audios.Add(audio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserEntityAffinities.Add(new UserEntityAffinity { UserId = 7, HostType = AffinityHostType.Audio, HostId = audio.Id, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 12 });
        context.UserBookmarks.Add(new UserBookmark { UserId = 7, HostType = AffinityHostType.Audio, HostId = audio.Id, CreatedAt = DateTime.UtcNow });
        context.Interactions.Add(new Interaction { UserId = 7, HostType = InteractionHostType.Audio, HostId = audio.Id, Kind = InteractionKind.PageVisit });
        context.PlaybackSessions.Add(new PlaybackSession { UserId = 7, HostType = InteractionHostType.Audio, HostId = audio.Id, SessionId = Guid.NewGuid() });
        context.Ratings.Add(new Rating { UserId = 7, HostType = RatingHostType.Audio, HostId = audio.Id, Aspect = "overall", Value = 80 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Audios.Remove(audio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await context.UserEntityAffinities.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.UserBookmarks.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.Interactions.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.PlaybackSessions.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.Ratings.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingEntity_RemovesEngagementRowsOwnedByOtherUsers()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var audio = new Audio { Title = "Delete across principals" };
        context.Audios.Add(audio);
        await context.SaveChangesAsync();
        foreach (var userId in new[] { 7, 8 })
        {
            context.UserEntityAffinities.Add(new UserEntityAffinity { UserId = userId, HostType = AffinityHostType.Audio, HostId = audio.Id });
            context.UserBookmarks.Add(new UserBookmark { UserId = userId, HostType = AffinityHostType.Audio, HostId = audio.Id, CreatedAt = DateTime.UtcNow });
            context.Interactions.Add(new Interaction { UserId = userId, HostType = InteractionHostType.Audio, HostId = audio.Id, Kind = InteractionKind.PageVisit });
            context.PlaybackSessions.Add(new PlaybackSession { UserId = userId, HostType = InteractionHostType.Audio, HostId = audio.Id, SessionId = Guid.NewGuid() });
            context.Ratings.Add(new Rating { UserId = userId, HostType = RatingHostType.Audio, HostId = audio.Id, Aspect = "overall", Value = 80 });
        }
        await context.SaveChangesAsync();
        scope.PrincipalAccessor.Set(CreatePrincipal(7));

        context.Audios.Remove(audio);
        await context.SaveChangesAsync();

        Assert.Empty(await context.UserEntityAffinities.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await context.UserBookmarks.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await context.Interactions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await context.PlaybackSessions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await context.Ratings.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DeletingUser_RemovesTheirEngagementRowsAndBookmarks()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var audio = new Audio { Title = "User cleanup audio" };
        var user = new User { Id = 17, Username = "cleanup-user", PasswordHash = "test" };
        context.AddRange(audio, user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserEntityAffinities.Add(new UserEntityAffinity { UserId = user.Id, HostType = AffinityHostType.Audio, HostId = audio.Id, LastConsumedAt = DateTime.UtcNow, LastPositionSec = 12 });
        context.UserBookmarks.Add(new UserBookmark { UserId = user.Id, HostType = AffinityHostType.Audio, HostId = audio.Id, CreatedAt = DateTime.UtcNow });
        context.Interactions.Add(new Interaction { UserId = user.Id, HostType = InteractionHostType.Audio, HostId = audio.Id, Kind = InteractionKind.PageVisit });
        context.PlaybackSessions.Add(new PlaybackSession { UserId = user.Id, HostType = InteractionHostType.Audio, HostId = audio.Id, SessionId = Guid.NewGuid() });
        context.Ratings.Add(new Rating { UserId = user.Id, HostType = RatingHostType.Audio, HostId = audio.Id, Aspect = "overall", Value = 80 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Users.Remove(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await context.UserEntityAffinities.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.UserBookmarks.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.Interactions.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.PlaybackSessions.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await context.Ratings.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DynamicGroupPagination_ReturnsRequestedPageAndTotal()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var firstVideo = new Video { Title = "First" };
        var secondVideo = new Video { Title = "Second" };
        var thirdVideo = new Video { Title = "Third" };
        var group = new Group { Name = "Save for Later", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.SaveForLaterSourceKey };
        context.AddRange(firstVideo, secondVideo, thirdVideo, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserBookmarks.AddRange(
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = firstVideo.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-30) },
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = secondVideo.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-20) },
            new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = thirdVideo.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-10) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 2, PerPage = 2 }, forceRefresh: true, CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Page);
        var item = Assert.Single(page.Items);
        Assert.Equal("First", item.Title);
    }

    [Fact]
    public async Task EnsureBuiltInGroupsAsync_CreatesMissingBuiltInGroups()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var resolver = CreateResolver(context, scope.PrincipalAccessor);

        await resolver.EnsureBuiltInGroupsAsync(CancellationToken.None);

        var groups = await context.Groups
            .OrderBy(group => group.Name)
            .Select(group => new { group.Name, group.QuerySourceKey, group.Kind })
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("Continue Watching", DynamicGroupResolver.ContinueWatchingSourceKey, GroupKind.Dynamic),
                ("Save for Later", DynamicGroupResolver.SaveForLaterSourceKey, GroupKind.Dynamic),
                ("Watch History", DynamicGroupResolver.WatchHistorySourceKey, GroupKind.Dynamic),
            ],
            groups.Select(group => (group.Name, group.QuerySourceKey, group.Kind)).ToArray());
    }

    [Fact]
    public async Task FilterDynamicGroupSource_UsesSavedVideoFilter()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var included = new Video { Title = "Included", Organized = true };
        var excluded = new Video { Title = "Excluded", Organized = false };
        var group = new Group
        {
            Name = "Organized Videos",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = "{\"entityType\":\"video\",\"findFilter\":{\"sort\":\"title\",\"direction\":\"asc\"},\"objectFilter\":{\"organized\":true}}",
        };
        context.AddRange(included, excluded, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(included.Id, item.VideoId);
        Assert.Equal("Included", item.Title);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_UsesUppercasePerformerCriterion()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var performer = new Performer { Name = "Matched Performer" };
        var included = new Video { Title = "Included" };
        included.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        var excluded = new Video { Title = "Excluded" };
        context.AddRange(included, excluded);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var group = new Group
        {
            Name = "Performer Videos",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = "{\"entityTypes\":[\"video\"],\"findFilters\":{\"video\":{\"sort\":\"title\",\"direction\":\"asc\"}},\"objectFilters\":{\"video\":{\"performersCriterion\":{\"value\":[" + performer.Id + "],\"modifier\":\"INCLUDES_ALL\"}}}}",
        };
        context.Add(group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(included.Id, item.VideoId);
        Assert.Equal("Included", item.Title);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_UsesSavedSegmentFilterAndSort()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var video = new Video { Title = "Host Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var shortIncluded = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 3, EndSec = 5, ImageBlobId = "short-cover", Title = "Short Included" };
        var longIncluded = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 4, EndSec = 14, ImageBlobId = "long-cover", Title = "Long Included" };
        var missingCover = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 5, EndSec = 20, Title = "Missing Cover" };
        var early = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 1, EndSec = 30, ImageBlobId = "early-cover", Title = "Early" };
        var group = new Group
        {
            Name = "Covered Segments",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = "{\"entityType\":\"segment\",\"findFilter\":{\"sort\":\"duration\",\"direction\":\"desc\"},\"objectFilter\":{\"hasImageCriterion\":{\"value\":true},\"startSecCriterion\":{\"value\":2,\"modifier\":\"GREATER_THAN\"}}}",
        };
        context.AddRange(shortIncluded, longIncluded, missingCover, early, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([longIncluded.Id, shortIncluded.Id], page.Items.Select(item => item.HostId).ToArray());
        Assert.All(page.Items, item => Assert.Equal("segment", item.HostType));
    }

    [Fact]
    public async Task FilterDynamicGroupSource_UsesSegmentRelationshipAndHostFilters()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var performer = new Performer { Name = "Matched Performer" };
        var tag = new Tag { Name = "Matched Tag" };
        var video = new Video { Title = "Host Video" };
        var otherVideo = new Video { Title = "Other Video" };
        context.AddRange(performer, tag, video, otherVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var alphaFace = new Face { Label = "Alpha Face", PerformerId = performer.Id };
        var betaFace = new Face { Label = "Beta Face", PerformerId = performer.Id };
        context.Faces.AddRange(alphaFace, betaFace);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var betaSegment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            StartSec = 10,
            EndSec = 12,
            TagId = tag.Id,
            Kind = "face",
            RefId = betaFace.Id,
            SourceKey = "ext:ai.faces",
            SourceRunId = "run-match",
            Title = "Beta Segment",
        };
        var alphaSegment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            StartSec = 20,
            EndSec = 22,
            TagId = tag.Id,
            Kind = "face",
            RefId = alphaFace.Id,
            SourceKey = "ext:ai.faces",
            SourceRunId = "run-match",
            Title = "Alpha Segment",
        };
        var excludedWrongSource = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            StartSec = 30,
            EndSec = 32,
            TagId = tag.Id,
            Kind = "face",
            RefId = alphaFace.Id,
            SourceKey = "user",
            SourceRunId = "run-match",
            Title = "Wrong Source",
        };
        var excludedWrongVideo = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = otherVideo.Id,
            StartSec = 40,
            EndSec = 42,
            TagId = tag.Id,
            Kind = "face",
            RefId = alphaFace.Id,
            SourceKey = "ext:ai.faces",
            SourceRunId = "run-match",
            Title = "Wrong Video",
        };
        var group = new Group
        {
            Name = "Relationship Segments",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "segment",
                findFilter = new FindFilter { Sort = "ref", Direction = SortDirection.Asc },
                objectFilter = new
                {
                    videoTitleCriterion = new StringCriterion { Value = "Host", Modifier = CriterionModifier.Includes },
                    videosCriterion = new MultiIdCriterion { Value = [video.Id], Modifier = CriterionModifier.Includes },
                    hostTypeCriterion = new StringCriterion { Value = "video", Modifier = CriterionModifier.Equals },
                    sourceCategoryCriterion = new StringCriterion { Value = "extensions", Modifier = CriterionModifier.Equals },
                    sourceRunIdCriterion = new StringCriterion { Value = "run-match", Modifier = CriterionModifier.Equals },
                    tagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
                    performersCriterion = new MultiIdCriterion { Value = [performer.Id], Modifier = CriterionModifier.Includes },
                    facesCriterion = new MultiIdCriterion { Value = [alphaFace.Id, betaFace.Id], Modifier = CriterionModifier.Includes },
                },
            }),
        };
        context.AddRange(betaSegment, alphaSegment, excludedWrongSource, excludedWrongVideo, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([alphaSegment.Id, betaSegment.Id], page.Items.Select(item => item.HostId).ToArray());
        Assert.All(page.Items, item => Assert.Equal("segment", item.HostType));
    }

    [Fact]
    public async Task FilterDynamicGroupSource_AppliesRequiredOnlySegmentRelations()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var requiredTag = new Tag { Name = "Required Tag" };
        var otherTag = new Tag { Name = "Other Tag" };
        var requiredVideo = new Video { Title = "Required Video" };
        var otherVideo = new Video { Title = "Other Video" };
        context.AddRange(requiredTag, otherTag, requiredVideo, otherVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var included = new Segment { HostType = SegmentHostType.Video, HostId = requiredVideo.Id, TagId = requiredTag.Id, StartSec = 1 };
        var wrongVideo = new Segment { HostType = SegmentHostType.Video, HostId = otherVideo.Id, TagId = requiredTag.Id, StartSec = 2 };
        var wrongTag = new Segment { HostType = SegmentHostType.Video, HostId = requiredVideo.Id, TagId = otherTag.Id, StartSec = 3 };
        var group = new Group
        {
            Name = "Required Segment Relations",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "segment",
                objectFilter = new
                {
                    videosCriterion = new MultiIdCriterion { RequiredIds = [requiredVideo.Id] },
                    tagsCriterion = new MultiIdCriterion { RequiredIds = [requiredTag.Id] },
                },
            }),
        };
        context.AddRange(included, wrongVideo, wrongTag, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(included.Id, item.HostId);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_AppliesScalarSegmentFaceAndPerformerSemantics()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var performerA = new Performer { Name = "Performer A" };
        var performerB = new Performer { Name = "Performer B" };
        context.AddRange(performerA, performerB);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var faceA = new Face { Label = "Face A", PerformerId = performerA.Id };
        var faceB = new Face { Label = "Face B", PerformerId = performerB.Id };
        var video = new Video { Title = "Host Video" };
        context.AddRange(faceA, faceB, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var faceSegmentA = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, Kind = "face", RefId = faceA.Id, StartSec = 1 };
        var faceSegmentB = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, Kind = "face", RefId = faceB.Id, StartSec = 2 };
        var performerSegmentA = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, Kind = "performer", RefId = performerA.Id, StartSec = 3 };
        var unrelatedSegment = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 4 };
        var group = new Group
        {
            Name = "Scalar Segment Relations",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
        };
        context.AddRange(faceSegmentA, faceSegmentB, performerSegmentA, unrelatedSegment, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);

        async Task<PaginatedResponse<GroupItemDto>> ResolveAsync(MultiIdCriterion? facesCriterion = null, MultiIdCriterion? performersCriterion = null)
        {
            group.QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "segment",
                objectFilter = new { facesCriterion, performersCriterion },
            });
            await context.SaveChangesAsync();
            return await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);
        }

        var faceIncludesAll = await ResolveAsync(facesCriterion: new MultiIdCriterion
        {
            Value = [faceA.Id, faceB.Id],
            Modifier = CriterionModifier.IncludesAll,
        });
        Assert.Empty(faceIncludesAll.Items);

        var faceExcludesAll = await ResolveAsync(facesCriterion: new MultiIdCriterion
        {
            Value = [faceA.Id, faceB.Id],
            Modifier = CriterionModifier.ExcludesAll,
        });
        Assert.Equal(4, faceExcludesAll.TotalCount);

        var requiredFace = await ResolveAsync(facesCriterion: new MultiIdCriterion { RequiredIds = [faceA.Id] });
        Assert.Equal(faceSegmentA.Id, Assert.Single(requiredFace.Items).HostId);

        var performerIncludesAll = await ResolveAsync(performersCriterion: new MultiIdCriterion
        {
            Value = [performerA.Id, performerB.Id],
            Modifier = CriterionModifier.IncludesAll,
        });
        Assert.Empty(performerIncludesAll.Items);

        var performerExcludesAll = await ResolveAsync(performersCriterion: new MultiIdCriterion
        {
            Value = [performerA.Id, performerB.Id],
            Modifier = CriterionModifier.ExcludesAll,
        });
        Assert.Equal(4, performerExcludesAll.TotalCount);

        var requiredPerformer = await ResolveAsync(performersCriterion: new MultiIdCriterion { RequiredIds = [performerA.Id] });
        Assert.Equal(
            [faceSegmentA.Id, performerSegmentA.Id],
            requiredPerformer.Items.Select(item => item.HostId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task FilterDynamicGroupSource_AppliesRequiredOnlyAudioTags()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var requiredTag = new Tag { Name = "Required Tag" };
        var otherTag = new Tag { Name = "Other Tag" };
        var included = new Audio { Title = "Included" };
        var excluded = new Audio { Title = "Excluded" };
        included.AudioTags.Add(new AudioTag { Audio = included, Tag = requiredTag });
        excluded.AudioTags.Add(new AudioTag { Audio = excluded, Tag = otherTag });
        var group = new Group
        {
            Name = "Required Audio Tag",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "audio",
                objectFilter = new
                {
                    tagsCriterion = new MultiIdCriterion { RequiredIds = new List<int>() },
                },
            }),
        };
        context.AddRange(requiredTag, otherTag, included, excluded, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        group.QueryJson = JsonSerializer.Serialize(new
        {
            entityType = "audio",
            objectFilter = new
            {
                tagsCriterion = new MultiIdCriterion { RequiredIds = [requiredTag.Id] },
            },
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(included.Id, item.HostId);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_ScopesAudioOccurrenceTagsToRequiredPerformer()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var target = new Performer { Name = "Target" };
        var other = new Performer { Name = "Other" };
        var tag = new Tag { Name = "Occurrence Tag" };
        var targetTagged = new Audio
        {
            Title = "Target tagged",
            AudioPerformers = [new AudioPerformer { Performer = target }],
        };
        var otherTagged = new Audio
        {
            Title = "Other tagged",
            AudioPerformers =
            [
                new AudioPerformer { Performer = target },
                new AudioPerformer { Performer = other },
            ],
        };
        context.AddRange(tag, targetTagged, otherTagged);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.TagApplications.AddRange(
            new TagApplication { HostType = AffinityHostType.Audio, HostId = targetTagged.Id, ContextType = "performer", ContextId = target.Id, TagId = tag.Id, SourceKey = "test" },
            new TagApplication { HostType = AffinityHostType.Audio, HostId = otherTagged.Id, ContextType = "performer", ContextId = other.Id, TagId = tag.Id, SourceKey = "test" });
        var group = new Group
        {
            Name = "Required Performer Occurrence",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "audio",
                objectFilter = new
                {
                    performersCriterion = new MultiIdCriterion { RequiredIds = [target.Id] },
                    performerTagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
                },
            }),
        };
        context.Add(group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        Assert.Equal(targetTagged.Id, Assert.Single(page.Items).HostId);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_ExpandsAudioTagHierarchy()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var parent = new Tag { Name = "Parent" };
        var child = new Tag { Name = "Child" };
        var included = new Audio { Title = "Included" };
        included.AudioTags.Add(new AudioTag { Audio = included, Tag = child });
        context.AddRange(parent, child, included);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Set<TagParent>().Add(new TagParent { ParentId = parent.Id, ChildId = child.Id });
        var group = new Group
        {
            Name = "Hierarchical Audio Tag",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = JsonSerializer.Serialize(new
            {
                entityType = "audio",
                objectFilter = new
                {
                    tagsCriterion = new MultiIdCriterion
                    {
                        Value = [parent.Id],
                        Modifier = CriterionModifier.Includes,
                        Depth = -1,
                    },
                },
            }),
        };
        context.Add(group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 10 }, forceRefresh: true, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(included.Id, item.HostId);
    }

    [Fact]
    public async Task FilterDynamicGroupSource_ReturnsTotalAcrossEntityTypesWhenPageIsFilled()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        for (var index = 0; index < 40; index++)
            context.Videos.Add(new Video { Title = $"Video {index:D2}" });
        for (var index = 0; index < 25; index++)
            context.Images.Add(new Image { Title = $"Image {index:D2}" });

        var group = new Group
        {
            Name = "Mixed Dynamic",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.FilterSourceKey,
            QueryJson = "{\"entityTypes\":[\"video\",\"image\"],\"findFilters\":{\"video\":{\"sort\":\"title\",\"direction\":\"asc\"},\"image\":{\"sort\":\"title\",\"direction\":\"asc\"}}}",
        };
        context.Groups.Add(group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor, includeFilterSource: true);
        var page = await resolver.ResolvePageDtosAsync(group.Id, new FindFilter { Page = 1, PerPage = 40 }, forceRefresh: true, CancellationToken.None);

        Assert.Equal(65, page.TotalCount);
        Assert.Equal(40, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal("video", item.HostType));
    }

    [Fact]
    public async Task SnapshotDynamicGroup_WritesStaticGroupItems()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        principalAccessor.Set(CreatePrincipal(7));

        var video = new Video { Title = "Snapshot Video" };
        var group = new Group { Name = "Saved Snapshot", Kind = GroupKind.Dynamic, QuerySourceKey = DynamicGroupResolver.SaveForLaterSourceKey };
        context.AddRange(video, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserBookmarks.Add(new UserBookmark { UserId = 7, HostType = AffinityHostType.Video, HostId = video.Id, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = CreateResolver(context, principalAccessor);
        await resolver.SnapshotAsync(group.Id, CancellationToken.None);

        var updatedGroup = await context.Groups.Include(item => item.GroupItems).SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GroupKind.Static, updatedGroup.Kind);
        Assert.Null(updatedGroup.QuerySourceKey);
        var item = Assert.Single(updatedGroup.GroupItems);
        Assert.Equal("video", item.HostType);
        Assert.Equal(video.Id, item.HostId);
        Assert.Equal(video.Id, item.VideoId);
        Assert.Equal(GroupItemKind.Video, item.Kind);
    }

    [Fact]
    public async Task GroupRepository_SortOrderSort_UsesManualOrder()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        context.Groups.AddRange(
            new Group { Name = "Second", SortOrder = 20 },
            new Group { Name = "First", SortOrder = 10 },
            new Group { Name = "Third", SortOrder = 30 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new GroupRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter { Sort = "sort_order", Direction = SortDirection.Asc, Page = 1, PerPage = 10 }, CancellationToken.None);

        Assert.Equal(3, totalCount);
        Assert.Equal(["First", "Second", "Third"], items.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task GroupRepository_PerformerCriterion_ComposesRequiredAndSavedPerformers()
    {
        await using var scope = CreateContext();
        var context = scope.Context;
        var target = new Performer { Name = "Target" };
        var saved = new Performer { Name = "Saved" };
        var targetVideo = new Video { Title = "Target Video", VideoPerformers = [new VideoPerformer { Performer = target }] };
        var savedVideo = new Video { Title = "Saved Video", VideoPerformers = [new VideoPerformer { Performer = saved }] };
        context.AddRange(targetVideo, savedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var targetOnly = new Group
        {
            Name = "Target only",
            GroupItems = [new GroupItem { Kind = GroupItemKind.Performer, HostType = "performer", HostId = target.Id }],
        };
        var savedOnly = new Group
        {
            Name = "Saved only",
            GroupItems = [new GroupItem { Kind = GroupItemKind.Video, HostType = "video", HostId = savedVideo.Id, VideoId = savedVideo.Id }],
        };
        var both = new Group
        {
            Name = "Both",
            GroupItems =
            [
                new GroupItem { Kind = GroupItemKind.Performer, HostType = "performer", HostId = target.Id },
                new GroupItem { Kind = GroupItemKind.Video, HostType = "video", HostId = savedVideo.Id, VideoId = savedVideo.Id },
            ],
        };
        context.AddRange(targetOnly, savedOnly, both);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new GroupRepository(context);
        var filter = new GroupFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                Value = [saved.Id],
                Modifier = CriterionModifier.Includes,
                RequiredIds = [target.Id],
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 10 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal("Both", Assert.Single(items).Name);
    }

    private static DynamicGroupResolver CreateResolver(CoveContext context, CurrentPrincipalAccessor principalAccessor, bool includeFilterSource = false)
    {
        var sources = new List<IDynamicGroupSource>
        {
            new SaveForLaterDynamicGroupSource(context),
            new WatchHistoryDynamicGroupSource(context),
            new ContinueWatchingDynamicGroupSource(context),
        };
        if (includeFilterSource)
            sources.Add(new FilterDynamicGroupSource(context, new VideoRepository(context), new ImageRepository(context)));

        return new DynamicGroupResolver(context, sources, principalAccessor);
    }

    private static CovePrincipal CreatePrincipal(int userId) => new()
    {
        UserId = userId,
        Username = $"user-{userId}",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string> { Permissions.All },
    };

    private static TestContextScope CreateContext()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"dynamic-groups-{Guid.NewGuid():N}")
            .Options;
        return new TestContextScope(new DynamicGroupTestContext(options, principalAccessor), principalAccessor);
    }

    private sealed class DynamicGroupTestContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor);

    private sealed class CommandRecorderInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TestContextScope(CoveContext context, CurrentPrincipalAccessor principalAccessor) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;
        public CurrentPrincipalAccessor PrincipalAccessor { get; } = principalAccessor;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            PrincipalAccessor.Set(null);
        }
    }
}
