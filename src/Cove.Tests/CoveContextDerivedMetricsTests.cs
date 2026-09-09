using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class CoveContextDerivedMetricsTests
{
    private const int TestUserId = 1;

    [Fact]
    public async Task SaveChangesAsync_NewVideoWithNewVideoFile_RefreshesFileMetricsAfterFirstSave()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = "E:/media/videos" };
        var video = new Video
        {
            Title = "Direct Video",
            Files =
            [
                new VideoFile
                {
                    Basename = "direct.mp4",
                    ParentFolder = folder,
                    Size = 1234,
                    Duration = 12.5,
                    Width = 1280,
                    Height = 720,
                    FrameRate = 30,
                    BitRate = 8000,
                    ModTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                }
            ],
        };

        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, video.FileCount);
        Assert.Equal(1234, video.MaxFileSize);
        Assert.Equal(12.5, video.MaxDuration);
        Assert.Equal(1280, video.MaxResolution);
        Assert.Equal("E:/media/videos/direct.mp4", video.MinPath);
    }

    [Fact]
    public async Task SaveChangesAsync_NewImageWithNewImageFile_RefreshesFileMetricsAfterFirstSave()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = "E:/media/images" };
        var image = new Image
        {
            Title = "Direct Image",
            Files =
            [
                new ImageFile
                {
                    Basename = "direct.jpg",
                    ParentFolder = folder,
                    Size = 5678,
                    Width = 640,
                    Height = 480,
                    ModTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                }
            ],
        };

        context.Images.Add(image);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, image.FileCount);
        Assert.Equal(5678, image.MaxFileSize);
        Assert.Equal(640, image.MaxResolution);
        Assert.Equal("E:/media/images/direct.jpg", image.MinPath);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"derived-metrics-{Guid.NewGuid():N}")
            .Options;

        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = TestUserId,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });

        return new CoveContext(options, principalAccessor);
    }
}
