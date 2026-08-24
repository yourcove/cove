using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities;

public sealed class NestedResourceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/groups/{groupId:int}/items")]
    [CoversEndpoint("GET", "/api/groups/{groupId:int}/items")]
    public async Task GivenGroupAndVideo_WhenVideoIsAdded_ThenGroupContainsVideoItem()
    {
        // Arrange
        var group = await AsUser().CreateGroupAsync("API test group", TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.MuchAdoAboutNothinOn.Title, TestContext.Current.CancellationToken);

        // Act
        var createdItem = await AsUser().AddVideoToGroupAsync(video, group, TestContext.Current.CancellationToken);

        // Assert
        var items = await AsUser().GetGroupItemsAsync(group, TestContext.Current.CancellationToken);
        items.Should().ContainSingle();
        items.Single().Id.Should().Be(createdItem.Id);
        items.Single().Kind.Should().Be(GroupItemKind.Video);
        items.Single().VideoId.Should().Be(video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/{imageId:int}/detections")]
    [CoversEndpoint("GET", "/api/images/{imageId:int}/detections")]
    public async Task GivenImage_WhenDetectionIsCreated_ThenImageContainsDetection()
    {
        // Arrange
        var image = await AsUser().CreateImageAsync("API test detection image", TestContext.Current.CancellationToken);

        // Act
        var createdDetection = await AsUser().CreateImageDetectionAsync(image, "subject", TestContext.Current.CancellationToken);

        // Assert
        var detections = await AsUser().GetImageDetectionsAsync(image, TestContext.Current.CancellationToken);
        detections.Should().ContainSingle();
        detections.Single().Id.Should().Be(createdDetection.Id);
        detections.Single().HostType.Should().Be(DetectionHostType.Image);
        detections.Single().Class.Should().Be("subject");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/detections")]
    [CoversEndpoint("GET", "/api/videos/{videoId:int}/detections")]
    public async Task GivenVideo_WhenDetectionIsCreated_ThenVideoContainsDetection()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheGoodTheBadAndTheShirtless.Title, TestContext.Current.CancellationToken);

        // Act
        var createdDetection = await AsUser().CreateVideoDetectionAsync(video, "subject", TestContext.Current.CancellationToken);

        // Assert
        var detections = await AsUser().GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken);
        detections.Should().ContainSingle();
        detections.Single().Id.Should().Be(createdDetection.Id);
        detections.Single().HostType.Should().Be(DetectionHostType.Video);
        detections.Single().Class.Should().Be("subject");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/segments")]
    [CoversEndpoint("GET", "/api/videos/{videoId:int}/segments")]
    public async Task GivenVideo_WhenSegmentIsCreated_ThenVideoContainsSegment()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheFastAndTheFlirtatious.Title, TestContext.Current.CancellationToken);

        // Act
        var createdSegment = await AsUser().CreateVideoSegmentAsync(video, "Opening", TestContext.Current.CancellationToken);

        // Assert
        var segments = await AsUser().GetVideoSegmentsAsync(video, TestContext.Current.CancellationToken);
        segments.Should().ContainSingle();
        segments.Single().Id.Should().Be(createdSegment.Id);
        segments.Single().HostType.Should().Be(SegmentHostType.Video);
        segments.Single().Title.Should().Be("Opening");
    }
}
