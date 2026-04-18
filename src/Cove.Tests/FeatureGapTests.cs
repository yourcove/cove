using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Api.Services;
using Xunit;

namespace Cove.Tests;

/// <summary>
/// Tests for features implemented to close the feature gaps:
/// Gallery Cover, Captions, Metadata Import DTOs, Scraper DTOs, Transcoding.
/// </summary>
public class FeatureGapTests
{
    private static TranscodeService CreateTranscodeService(CoveConfiguration? config = null)
    {
        config ??= new CoveConfiguration();
        var logger = new NullLogger<TranscodeService>();
        return new TranscodeService(config, logger);
    }

    // ── Gallery Cover DTO Tests ─────────────────────────────────

    [Fact]
    public void Gallery_HasCoverProperties()
    {
        var gallery = new Gallery { Title = "Test", ImageBlobId = "blob-123", CoverImageId = 42 };
        Assert.Equal("blob-123", gallery.ImageBlobId);
        Assert.Equal(42, gallery.CoverImageId);
    }

    [Fact]
    public void Gallery_CoverDefaults()
    {
        var gallery = new Gallery { Title = "Default Cover" };
        Assert.Null(gallery.ImageBlobId);
        Assert.Null(gallery.CoverImageId);
    }

    [Fact]
    public void GallerySetCoverDto_Works()
    {
        var dto = new GallerySetCoverDto(42);
        Assert.Equal(42, dto.ImageId);
    }

    // ── Caption / Subtitle Tests ────────────────────────────────

    [Fact]
    public void VideoCaption_EntityCreation()
    {
        var caption = new VideoCaption
        {
            Id = 1,
            FileId = 10,
            LanguageCode = "en",
            CaptionType = "vtt",
            Filename = "scene.en.vtt"
        };
        Assert.Equal(1, caption.Id);
        Assert.Equal(10, caption.FileId);
        Assert.Equal("en", caption.LanguageCode);
        Assert.Equal("vtt", caption.CaptionType);
        Assert.Equal("scene.en.vtt", caption.Filename);
    }

    [Fact]
    public void CaptionDto_RecordCreation()
    {
        var dto = new CaptionDto(1, "fr", "srt", "movie.fr.srt");
        Assert.Equal(1, dto.Id);
        Assert.Equal("fr", dto.LanguageCode);
        Assert.Equal("srt", dto.CaptionType);
        Assert.Equal("movie.fr.srt", dto.Filename);
    }

    [Fact]
    public void VideoFileDto_IncludesCaptions()
    {
        var captions = new List<CaptionDto> { new(1, "en", "vtt", "test.en.vtt") };
        var dto = new VideoFileDto(
            Id: 1, Path: "/test.mp4", Basename: "test.mp4", Format: "mp4",
            Width: 1920, Height: 1080, Duration: 120.0,
            VideoCodec: "h264", AudioCodec: "aac",
            FrameRate: 24.0, BitRate: 5000000, Size: 1000,
            Fingerprints: [],
            Captions: captions
        );
        Assert.NotNull(dto.Captions);
        Assert.Single(dto.Captions);
        Assert.Equal("en", dto.Captions[0].LanguageCode);
    }

    [Fact]
    public void VideoFileDto_CaptionsOptional()
    {
        var dto = new VideoFileDto(
            Id: 1, Path: "/test.mp4", Basename: "test.mp4", Format: "mp4",
            Width: 1920, Height: 1080, Duration: 120.0,
            VideoCodec: "h264", AudioCodec: "aac",
            FrameRate: 24.0, BitRate: 5000000, Size: 1000,
            Fingerprints: []
        );
        Assert.Null(dto.Captions);
    }

    // ── Scraper DTO Tests ───────────────────────────────────────

    [Fact]
    public void ScrapeUrlRequest_RecordCreation()
    {
        var req = new ScrapeUrlRequest("test-scraper", "scene", "https://example.com/scene/123");
        Assert.Equal("test-scraper", req.ScraperId);
        Assert.Equal("scene", req.EntityType);
        Assert.Equal("https://example.com/scene/123", req.Url);
    }

    [Fact]
    public void ScrapeNameRequest_RecordCreation()
    {
        var req = new ScrapeNameRequest("test-scraper", "scene", "Search Term");
        Assert.Equal("test-scraper", req.ScraperId);
        Assert.Equal("scene", req.EntityType);
        Assert.Equal("Search Term", req.Name);
    }

    [Fact]
    public void ScrapeFragmentRequest_RecordCreation()
    {
        var data = new Dictionary<string, object> { ["title"] = "Test Scene" };
        var req = new ScrapeFragmentRequest("test-scraper", "scene", data);
        Assert.Equal("test-scraper", req.ScraperId);
        Assert.Equal("scene", req.EntityType);
        Assert.Equal("Test Scene", req.Fragment["title"]);
    }

    // ── Transcode Service Tests ─────────────────────────────────

    [Fact]
    public void TranscodeService_GetAvailableResolutions_1080p()
    {
        var svc = CreateTranscodeService();
        var resolutions = svc.GetAvailableResolutions(1920, 1080);
        Assert.Contains("240p", resolutions);
        Assert.Contains("480p", resolutions);
        Assert.Contains("720p", resolutions);
        Assert.Contains("1080p", resolutions);
        Assert.DoesNotContain("1440p", resolutions);
        Assert.DoesNotContain("4K", resolutions);
    }

    [Fact]
    public void TranscodeService_GetAvailableResolutions_480p()
    {
        var svc = CreateTranscodeService();
        var resolutions = svc.GetAvailableResolutions(640, 480);
        Assert.Contains("240p", resolutions);
        Assert.Contains("360p", resolutions);
        Assert.Contains("480p", resolutions);
        Assert.DoesNotContain("720p", resolutions);
    }

    [Fact]
    public void TranscodeService_GetAvailableResolutions_MaxStreamingSize()
    {
        var svc = CreateTranscodeService(new CoveConfiguration { MaxStreamingTranscodeSize = 720 });
        var resolutions = svc.GetAvailableResolutions(3840, 2160);
        Assert.Contains("720p", resolutions);
        Assert.DoesNotContain("1080p", resolutions);
        Assert.DoesNotContain("4K", resolutions);
    }

    [Fact]
    public void TranscodeService_GetAvailableResolutions_4K()
    {
        var svc = CreateTranscodeService();
        var resolutions = svc.GetAvailableResolutions(3840, 2160);
        Assert.Contains("4K", resolutions);
        Assert.Contains("1440p", resolutions);
        Assert.Contains("1080p", resolutions);
        Assert.Equal(7, resolutions.Length); // 240p, 360p, 480p, 720p, 1080p, 1440p, 4K
    }

    [Fact]
    public void TranscodeService_GetAvailableResolutions_ZeroMaxStreamingSize()
    {
        // MaxStreamingTranscodeSize = 0 means original (no limit)
        var svc = CreateTranscodeService(new CoveConfiguration { MaxStreamingTranscodeSize = 0 });
        var resolutions = svc.GetAvailableResolutions(3840, 2160);
        // 0 means use source height, so all should be available
        Assert.Contains("4K", resolutions);
    }

    // ── Metadata Import Entity Tests ────────────────────────────

    [Fact]
    public void Tag_CanBeCreated()
    {
        var tag = new Tag { Name = "imported-tag" };
        Assert.Equal("imported-tag", tag.Name);
    }

    [Fact]
    public void Studio_CanBeCreated()
    {
        var studio = new Studio { Name = "imported-studio", Details = "test details" };
        Assert.Equal("imported-studio", studio.Name);
        Assert.Equal("test details", studio.Details);
    }

    [Fact]
    public void Performer_CanBeCreated()
    {
        var performer = new Performer { Name = "imported-performer", Country = "US" };
        Assert.Equal("imported-performer", performer.Name);
        Assert.Equal("US", performer.Country);
    }

    [Fact]
    public void Group_Synopsis_NotDescription()
    {
        var group = new Group { Name = "imported-group", Synopsis = "A test group", Director = "Test Director" };
        Assert.Equal("imported-group", group.Name);
        Assert.Equal("A test group", group.Synopsis);
        Assert.Equal("Test Director", group.Director);
    }
}
