using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public class VideoSubVideoCreationTests
{
    [Fact]
    public async Task VideosController_Create_AllowsNestedSubVideosUsingRelativeClipOffsets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync();

        var sourceVideo = new Video
        {
            Title = "Source Video",
            MaxDuration = 120,
        };
        var childVideo = new Video
        {
            Title = "Child Video",
            ParentVideo = sourceVideo,
            ClipStartSec = 30,
            ClipEndSec = 60,
            MaxDuration = 30,
        };

        context.Videos.AddRange(sourceVideo, childVideo);
        await context.SaveChangesAsync();

        var controller = new VideosController(
            new VideoRepository(context),
            context,
            null!,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            null!,
            null!,
            null!,
            new NoOpUserEngagementService(),
            new CustomFieldService(context),
            null,
            principalAccessor);

        var createResult = await controller.Create(new VideoCreateDto(
            Title: "Nested Video",
            Code: null,
            Details: null,
            Director: null,
            Date: null,
            Rating: null,
            Organized: false,
            StudioId: null,
            Captions: null,
            Urls: null,
            TagIds: null,
            PerformerIds: null,
            GalleryIds: null,
            Groups: null,
            CustomFields: null,
            ParentVideoId: childVideo.Id,
            ClipStartSec: 5,
            ClipEndSec: 10), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(createResult.Result);
        var createdDto = Assert.IsType<VideoDto>(created.Value);

        Assert.Equal(sourceVideo.Id, createdDto.ParentVideoId);
        Assert.Equal(35, createdDto.ClipStartSec);
        Assert.Equal(40, createdDto.ClipEndSec);

        var storedVideo = await context.Videos.SingleAsync(video => video.Id == createdDto.Id);
        Assert.Equal(sourceVideo.Id, storedVideo.ParentVideoId);
        Assert.Equal(35, storedVideo.ClipStartSec);
        Assert.Equal(40, storedVideo.ClipEndSec);
    }
}
