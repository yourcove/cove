using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FaceReviewAndSimilarityApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/faces/review/unlinked")]
    public async Task GivenMixedFaceStates_WhenUnlinkedReviewIsRead_ThenOnlyReviewableFacesAreReturned()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var eligible = await AsUser().CreateFaceAsync(new FaceCreateDto("Review candidate", null, false, null), TestContext.Current.CancellationToken);
        var linked = await AsUser().CreateFaceAsync(new FaceCreateDto("Linked candidate", performer.Id, false, null), TestContext.Current.CancellationToken);
        var ignored = await AsUser().CreateFaceAsync(new FaceCreateDto("Ignored candidate", null, true, null), TestContext.Current.CancellationToken);
        var merged = await AsUser().CreateFaceAsync(new FaceCreateDto("Merged candidate", null, false, null), TestContext.Current.CancellationToken);
        var mergeTarget = await AsUser().CreateFaceAsync(new FaceCreateDto("Merge survivor", null, false, null), TestContext.Current.CancellationToken);
        await AsUser().MergeFaceIntoAsync(merged.Id, mergeTarget.Id, TestContext.Current.CancellationToken);

        // Act
        var review = await AsUser(ApiTestUsers.Eva).GetUnlinkedFaceReviewAsync(take: 100, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        review.Select(face => face.Id).Should().BeEquivalentTo([eligible.Id, mergeTarget.Id]);
        review.Should().OnlyContain(face =>
            face.PerformerId == null
            && !face.Ignored
            && face.MergedIntoFaceId == null);
        review.Should().NotContain(face => face.Id == linked.Id || face.Id == ignored.Id || face.Id == merged.Id);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/faces/{id:int}/similar")]
    public async Task GivenFaceEmbeddings_WhenSimilarFacesAreRead_ThenVisibleMatchesAreRankedAndFiltered()
    {
        // Arrange
        const string kindFamily = "face.api-test.v1";
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Similarity source", null, false, null), TestContext.Current.CancellationToken);
        var nearest = await AsUser().CreateFaceAsync(new FaceCreateDto("Selected nearest", null, false, null), TestContext.Current.CancellationToken);
        var farther = await AsUser().CreateFaceAsync(new FaceCreateDto("Selected farther", null, false, null), TestContext.Current.CancellationToken);
        var hiddenByQuery = await AsUser().CreateFaceAsync(new FaceCreateDto("Hidden candidate", null, false, null), TestContext.Current.CancellationToken);
        var mergedCandidate = await AsUser().CreateFaceAsync(new FaceCreateDto("Selected merged", null, false, null), TestContext.Current.CancellationToken);
        var mergeTarget = await AsUser().CreateFaceAsync(new FaceCreateDto("Merged survivor", null, false, null), TestContext.Current.CancellationToken);
        var hostImage = await AsUser().CreateImageAsync($"Similarity host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var nearestDetection = await AsUser().CreateImageFaceDetectionAsync(hostImage, nearest, TestContext.Current.CancellationToken);
        await AsUser().MergeFaceIntoAsync(mergedCandidate.Id, mergeTarget.Id, TestContext.Current.CancellationToken);

        await AsDbUser().CreateFaceEmbeddingAsync(source.Id, [1f, 0f, 0f], kindFamily, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceEmbeddingAsync(nearest.Id, [0.99f, 0.01f, 0f], kindFamily, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceEmbeddingAsync(farther.Id, [0.8f, 0.2f, 0f], kindFamily, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceEmbeddingAsync(hiddenByQuery.Id, [0.9f, 0.1f, 0f], kindFamily, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceEmbeddingAsync(mergedCandidate.Id, [0.999f, 0.001f, 0f], kindFamily, TestContext.Current.CancellationToken);

        // Act
        var similar = await AsUser(ApiTestUsers.Eva).GetSimilarFacesAsync(source.Id, kindFamily, query: "Selected", perPage: 10, candidateCount: 10, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        similar.TotalCount.Should().Be(2);
        similar.Items.Select(face => face.Id).Should().Equal(nearest.Id, farther.Id);
        similar.Items[0].Distance.Should().BeLessThan(similar.Items[1].Distance);
        similar.Items[0].CoverImageUrl.Should().Contain($"/api/stream/detection/{nearestDetection.Id}/crop");
        similar.Items.Should().NotContain(face => face.Id == hiddenByQuery.Id || face.Id == mergedCandidate.Id);
    }
}
