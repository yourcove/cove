using Cove.Api.Controllers;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane2Collection.Name)]
public sealed class NestedResourceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoints(typeof(GroupItemsController))]
    public async Task GivenGroupAndVideo_WhenVideoIsAdded_ThenGroupContainsVideoItem()
    {
        // Arrange
        var group = await AsUser().CreateGroupAsync("API test group");
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.MuchAdoAboutNothinOn.Title);

        // Act
        var createdItem = await AsUser().AddVideoToGroupAsync(video, group);

        // Assert
        var items = await AsUser().GetGroupItemsAsync(group);
        items.Should().ContainSingle();
        items.Single().Id.Should().Be(createdItem.Id);
        items.Single().Kind.Should().Be(GroupItemKind.Video);
        items.Single().VideoId.Should().Be(video.Id);
    }

    [Fact]
    [CoversEndpoints(typeof(ImageDetectionsController))]
    public async Task GivenImage_WhenDetectionIsCreated_ThenImageContainsDetection()
    {
        // Arrange
        var image = await AsUser().CreateImageAsync("API test detection image");

        // Act
        var createdDetection = await AsUser().CreateImageDetectionAsync(image, "subject");

        // Assert
        var detections = await AsUser().GetImageDetectionsAsync(image);
        detections.Should().ContainSingle();
        detections.Single().Id.Should().Be(createdDetection.Id);
        detections.Single().HostType.Should().Be(DetectionHostType.Image);
        detections.Single().Class.Should().Be("subject");
    }

    [Fact]
    [CoversEndpoints(typeof(VideoDetectionsController))]
    public async Task GivenVideo_WhenDetectionIsCreated_ThenVideoContainsDetection()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheGoodTheBadAndTheShirtless.Title);

        // Act
        var createdDetection = await AsUser().CreateVideoDetectionAsync(video, "subject");

        // Assert
        var detections = await AsUser().GetVideoDetectionsAsync(video);
        detections.Should().ContainSingle();
        detections.Single().Id.Should().Be(createdDetection.Id);
        detections.Single().HostType.Should().Be(DetectionHostType.Video);
        detections.Single().Class.Should().Be("subject");
    }

    [Fact]
    [CoversEndpoints(typeof(VideoSegmentsController))]
    public async Task GivenVideo_WhenSegmentIsCreated_ThenVideoContainsSegment()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheFastAndTheFlirtatious.Title);

        // Act
        var createdSegment = await AsUser().CreateVideoSegmentAsync(video, "Opening");

        // Assert
        var segments = await AsUser().GetVideoSegmentsAsync(video);
        segments.Should().ContainSingle();
        segments.Single().Id.Should().Be(createdSegment.Id);
        segments.Single().HostType.Should().Be(SegmentHostType.Video);
        segments.Single().Title.Should().Be("Opening");
    }
}
