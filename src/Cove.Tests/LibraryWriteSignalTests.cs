using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public sealed class LibraryWriteSignalTests
{
    [Fact]
    public void ExpiresATokenTakenBeforeTheWriteButNotOneTakenAfter()
    {
        var signal = new LibraryWriteSignal();
        var before = signal.GetChangeToken();

        signal.Signal();

        Assert.True(before.HasChanged);
        Assert.False(signal.GetChangeToken().HasChanged);
    }

    [Fact]
    [RequiresPostgres]
    public async Task SignalsOnlyWhenASaveWritesSomething()
    {
        var signal = new LibraryWriteSignal();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(database, signal);
        var token = signal.GetChangeToken();

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(token.HasChanged);

        context.Videos.Add(new Video { Title = "Written" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(token.HasChanged);
    }

    // The tagger saves a video and immediately refreshes a list filtered to unsaved videos. The list
    // response was cached for a second keyed only on the query, so the refresh was served the page from
    // before the save and the saved video stayed listed.
    [Fact]
    [RequiresPostgres]
    public async Task FindPost_ReflectsAWriteMadeInsideTheCacheWindow()
    {
        var signal = new LibraryWriteSignal();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(database, signal);
        var saved = new Video { Title = "Saved by the tagger" };
        context.Videos.AddRange(saved, new Video { Title = "Still unsaved" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = new VideosController(
            new VideoRepository(context), context, null!, null!, null!, new MemoryCache(new MemoryCacheOptions()), null!, null!,
            new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus(), libraryWriteSignal: signal);
        var query = new VideoFilteredQueryRequest { ObjectFilter = new VideoFilter { Organized = false } };

        Assert.Equal(2, await CountAsync(controller, query));

        saved.Organized = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountAsync(controller, query));
    }

    private static async Task<int> CountAsync(VideosController controller, VideoFilteredQueryRequest query)
    {
        var response = Assert.IsType<OkObjectResult>(await controller.FindPost(query, TestContext.Current.CancellationToken));
        var page = Assert.IsType<PaginatedResponse<VideoDto>>(response.Value);
        Assert.Equal(page.TotalCount, page.Items.Count);
        return page.TotalCount;
    }

    private static CoveContext CreateContext(CovePostgresDatabase database, LibraryWriteSignal signal)
    {
        var options = database.CreateOptions(builder => builder.AddInterceptors(new LibraryWriteSignalInterceptor(signal)));
        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });
        return new CoveContext(options, principalAccessor);
    }
}
