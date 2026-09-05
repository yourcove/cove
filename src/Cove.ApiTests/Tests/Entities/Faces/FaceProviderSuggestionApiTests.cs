using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FaceProviderSuggestionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/faces/{id:int}/suggestions")]
    public async Task GivenPlannedProviderSuggestions_WhenMemberReadsAnUnlinkedFace_ThenRankedDeduplicatedAndCappedSuggestionsAreReturned()
    {
        // Arrange
        var highest = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Highest planned performer").Build(), TestContext.Current.CancellationToken);
        var second = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Second planned performer").Build(), TestContext.Current.CancellationToken);
        var third = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Third planned performer").Build(), TestContext.Current.CancellationToken);
        var candidate = await AsUser().CreateFaceAsync(new FaceCreateDto("Provider candidate", null, false, null), TestContext.Current.CancellationToken);
        var linked = await AsUser().CreateFaceAsync(new FaceCreateDto("Already linked", highest.Id, false, null), TestContext.Current.CancellationToken);
        await ConfigureFaceSuggestionPlanAsync(new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>
        {
            [candidate.Id] =
            [
                Suggest(third, 0.4f),
                Suggest(highest, 0.6f),
                Suggest(highest, 0.95f, evidenceCount: 2),
                Suggest(second, 0.9f),
            ],
        }, TestContext.Current.CancellationToken);

        // Act
        var suggestions = await AsUser(ApiTestUsers.Eva).GetFaceSuggestionsAsync(candidate.Id, maxResults: 2, cancellationToken: TestContext.Current.CancellationToken);
        var linkedSuggestions = await AsUser(ApiTestUsers.Eva).GetFaceSuggestionsAsync(linked.Id, cancellationToken: TestContext.Current.CancellationToken);
        var missing = () => AsUser(ApiTestUsers.Eva).GetFaceSuggestionsAsync(int.MaxValue);

        // Assert
        suggestions.Select(item => item.PerformerId).Should().Equal(highest.Id, second.Id);
        suggestions.Select(item => item.Confidence).Should().Equal(0.95f, 0.9f);
        suggestions[0].Evidence.Should().HaveCount(2);
        linkedSuggestions.Should().BeEmpty();
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/faces/batch/link-top-suggestion")]
    public async Task GivenMixedPlannedSuggestions_WhenMemberBatchLinksTopMatches_ThenEligibleFacesAndHostPropagationAreExact()
    {
        // Arrange
        var top = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Batch top performer").Build(), TestContext.Current.CancellationToken);
        var competing = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Batch competing performer").Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync($"Batch suggestion host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var linkable = await AsUser().CreateFaceAsync(new FaceCreateDto("Linkable face", null, false, null), TestContext.Current.CancellationToken);
        var alreadyLinked = await AsUser().CreateFaceAsync(new FaceCreateDto("Already linked face", top.Id, false, null), TestContext.Current.CancellationToken);
        var withoutSuggestion = await AsUser().CreateFaceAsync(new FaceCreateDto("No suggestion face", null, false, null), TestContext.Current.CancellationToken);
        var conflict = await AsUser().CreateFaceAsync(new FaceCreateDto("Conflicting face", null, false, null), TestContext.Current.CancellationToken);
        await AsUser().CreateVideoFaceDetectionAsync(video, linkable, TestContext.Current.CancellationToken);
        await ConfigureFaceSuggestionPlanAsync(new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>
        {
            [linkable.Id] =
            [
                Suggest(competing, 0.4f),
                Suggest(top, 0.94f),
                Suggest(top, 0.8f),
            ],
            [conflict.Id] =
            [
                Suggest(competing, 0.93f, conflictGroupId: "planned-conflict"),
                Suggest(top, 0.9f, conflictGroupId: "planned-conflict"),
            ],
        }, TestContext.Current.CancellationToken);

        // Act
        var defaultResult = await AsUser(ApiTestUsers.Eva).BatchLinkTopSuggestionAsync(new FaceBatchLinkTopSuggestionDto([linkable.Id, linkable.Id, alreadyLinked.Id, withoutSuggestion.Id, conflict.Id, int.MaxValue]), TestContext.Current.CancellationToken);
        var conflictResult = await AsUser(ApiTestUsers.Eva).BatchLinkTopSuggestionAsync(new FaceBatchLinkTopSuggestionDto([conflict.Id], LinkConflicting: true), TestContext.Current.CancellationToken);
        var linked = await AsUser(ApiTestUsers.Eva).GetFaceByIdAsync(linkable.Id, TestContext.Current.CancellationToken);
        var conflictLinked = await AsUser(ApiTestUsers.Eva).GetFaceByIdAsync(conflict.Id, TestContext.Current.CancellationToken);
        var propagatedVideos = await AsUser(ApiTestUsers.Eva).GetVideosByPerformerAsync(top.Id, TestContext.Current.CancellationToken);

        // Assert
        defaultResult.Succeeded.Should().Equal(linkable.Id);
        defaultResult.Failed.Should().BeEmpty();
        defaultResult.Skipped.Should().HaveCount(4);
        defaultResult.Skipped.Should().Contain(item => item.FaceId == alreadyLinked.Id && item.Reason == "Face is already linked.");
        defaultResult.Skipped.Should().Contain(item => item.FaceId == withoutSuggestion.Id && item.Reason == "No linkable top suggestion was available.");
        defaultResult.Skipped.Should().Contain(item => item.FaceId == conflict.Id && item.Reason == "Face has conflicting matches.");
        defaultResult.Skipped.Should().Contain(item => item.FaceId == int.MaxValue && item.Reason == "Face was not found.");
        linked.PerformerId.Should().Be(top.Id);
        propagatedVideos.Should().Contain(item => item.Id == video.Id);

        conflictResult.Succeeded.Should().Equal(conflict.Id);
        conflictResult.Skipped.Should().BeEmpty();
        conflictResult.Failed.Should().BeEmpty();
        conflictLinked.PerformerId.Should().Be(competing.Id);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/faces/review/ai-run")]
    public async Task GivenCompletedMediaRunEvidence_WhenMemberReviewsTheRun_ThenOnlyTheSingleVisibleReviewableTargetIsReturned()
    {
        // Arrange
        var reviewVideo = await AsUser().CreateVideoAsync($"AI review host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var included = await AsUser().CreateFaceAsync(new FaceCreateDto("Reviewable run face", null, false, null), TestContext.Current.CancellationToken);
        var ignored = await AsUser().CreateFaceAsync(new FaceCreateDto("Ignored run face", null, true, null), TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Run-linked performer").Build(), TestContext.Current.CancellationToken);
        var linked = await AsUser().CreateFaceAsync(new FaceCreateDto("Linked run face", performer.Id, false, null), TestContext.Current.CancellationToken);
        var startedAt = DateTime.UtcNow.AddMinutes(-3);
        var completedAt = startedAt.AddMinutes(1);
        var matchingRunKey = $"api-test-run-{Guid.NewGuid():N}";
        await AsDbUser().CreateFaceAppearanceAsync(included.Id, FaceAppearanceHostType.Video, reviewVideo.Id, 4, 3, 1, 1, 8, 0.97f, sourceRunId: matchingRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(ignored.Id, FaceAppearanceHostType.Video, reviewVideo.Id, 3, 2, 1, 2, 7, 0.95f, sourceRunId: matchingRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(linked.Id, FaceAppearanceHostType.Video, reviewVideo.Id, 2, 1, 1, 3, 6, 0.9f, sourceRunId: matchingRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateCompletedAiRunAsync(matchingRunKey, AiRunTargetType.Video, reviewVideo.Id, startedAt, completedAt, TestContext.Current.CancellationToken);

        var hiddenVideo = await AsUser().CreateVideoAsync($"Hidden AI review host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var hiddenFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Hidden run face", null, false, null), TestContext.Current.CancellationToken);
        var hiddenRunKey = $"api-test-hidden-run-{Guid.NewGuid():N}";
        await AsDbUser().CreateFaceAppearanceAsync(hiddenFace.Id, FaceAppearanceHostType.Video, hiddenVideo.Id, 2, 1, 1, 1, 2, 0.91f, sourceRunId: hiddenRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateCompletedAiRunAsync(hiddenRunKey, AiRunTargetType.Video, hiddenVideo.Id, startedAt, completedAt, TestContext.Current.CancellationToken);
        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Video,
            hiddenVideo.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"), TestContext.Current.CancellationToken);

        var outOfWindowVideo = await AsUser().CreateVideoAsync($"Out of window AI review host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var outOfWindowFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Out of window run face", null, false, null), TestContext.Current.CancellationToken);
        var outOfWindowRunKey = $"api-test-old-run-{Guid.NewGuid():N}";
        await AsDbUser().CreateFaceAppearanceAsync(outOfWindowFace.Id, FaceAppearanceHostType.Video, outOfWindowVideo.Id, 2, 1, 1, 1, 2, 0.8f, sourceRunId: outOfWindowRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateCompletedAiRunAsync(outOfWindowRunKey, AiRunTargetType.Video, outOfWindowVideo.Id, startedAt.AddHours(-2), completedAt.AddHours(-2), TestContext.Current.CancellationToken);

        var nonMediaVideo = await AsUser().CreateVideoAsync($"Non-media AI review host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var nonMediaFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Non-media run face", null, false, null), TestContext.Current.CancellationToken);
        var nonMediaRunKey = $"api-test-non-media-run-{Guid.NewGuid():N}";
        await AsDbUser().CreateFaceAppearanceAsync(nonMediaFace.Id, FaceAppearanceHostType.Video, nonMediaVideo.Id, 2, 1, 1, 1, 2, 0.8f, sourceRunId: nonMediaRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateCompletedAiRunAsync(nonMediaRunKey, AiRunTargetType.Performer, performer.Id, startedAt, completedAt, TestContext.Current.CancellationToken);

        await ConfigureFaceSuggestionPlanAsync(new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>
        {
            [included.Id] = [Suggest(performer, 0.96f)],
            [hiddenFace.Id] = [Suggest(performer, 0.99f)],
            [outOfWindowFace.Id] = [Suggest(performer, 0.8f)],
            [nonMediaFace.Id] = [Suggest(performer, 0.8f)],
        }, TestContext.Current.CancellationToken);

        // Act
        var noWindow = await AsUser(ApiTestUsers.Eva).GetAiRunFaceReviewAsync(startedAt: null, completedAt: null, cancellationToken: TestContext.Current.CancellationToken);
        var review = await AsUser(ApiTestUsers.Eva).GetAiRunFaceReviewAsync(startedAt, completedAt, take: 200, cancellationToken: TestContext.Current.CancellationToken);
        var deniedVideo = () => AsUser(ApiTestUsers.Eva).GetVideoByIdAsync(hiddenVideo.Id);

        // Assert
        noWindow.Should().BeEmpty();
        await deniedVideo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        review.Should().ContainSingle();
        review.Single().Id.Should().Be(included.Id);
        review.Single().TopSuggestion.Should().NotBeNull();
        review.Single().TopSuggestion!.PerformerId.Should().Be(performer.Id);

        // A second matching media target intentionally makes the run-review selection ambiguous.
        var secondTargetVideo = await AsUser().CreateVideoAsync($"Second AI review host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var secondTargetFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Second target run face", null, false, null), TestContext.Current.CancellationToken);
        var secondTargetRunKey = $"api-test-second-run-{Guid.NewGuid():N}";
        await AsDbUser().CreateFaceAppearanceAsync(secondTargetFace.Id, FaceAppearanceHostType.Video, secondTargetVideo.Id, 2, 1, 1, 1, 2, 0.8f, sourceRunId: secondTargetRunKey, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateCompletedAiRunAsync(secondTargetRunKey, AiRunTargetType.Video, secondTargetVideo.Id, startedAt, completedAt, TestContext.Current.CancellationToken);
        await ConfigureFaceSuggestionPlanAsync(new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>
        {
            [included.Id] = [Suggest(performer, 0.96f)],
            [secondTargetFace.Id] = [Suggest(performer, 0.85f)],
        }, TestContext.Current.CancellationToken);

        var ambiguousReview = await AsUser(ApiTestUsers.Eva).GetAiRunFaceReviewAsync(startedAt, completedAt, cancellationToken: TestContext.Current.CancellationToken);
        ambiguousReview.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("GET", "/api/faces")]
    public async Task GivenARejectedSuggestionRefreshedIntoTheCachedProjection_WhenTheFacesListIsRead_ThenTheRejectionStandsAndTheNextBestMatchIsShown()
    {
        // Arrange
        var rejectedPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Rejected cached performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var nextBest = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Next best performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var label = $"Rejected suggestion candidate {Guid.NewGuid():N}";
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto(label, null, false, null), TestContext.Current.CancellationToken);
        await ConfigureFaceSuggestionPlanAsync(new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>
        {
            [face.Id] = [Suggest(rejectedPerformer, 0.72f), Suggest(nextBest, 0.324f)],
        }, TestContext.Current.CancellationToken);

        var member = AsUser(ApiTestUsers.Eva);
        await member.RecordFaceSuggestionDecisionAsync(face.Id, new FaceSuggestionDecisionDto(rejectedPerformer.Id, FaceSuggestionDecisionValues.Reject), TestContext.Current.CancellationToken);

        // The projection is global and is recomputed from the global ranking, so the rejected performer
        // lands back in the cached columns — the state that used to resurface it on the faces list.
        await AsDbUser().MaterializeFaceTopSuggestionAsync(face.Id, rejectedPerformer.Id, rejectedPerformer.Name, 0.72f, TestContext.Current.CancellationToken);

        // Act
        var listed = await member.FindFacesAsync(label: label, cancellationToken: TestContext.Current.CancellationToken);
        var detail = await member.GetFaceByIdAsync(face.Id, TestContext.Current.CancellationToken);
        var live = await member.GetFaceSuggestionsAsync(face.Id, cancellationToken: TestContext.Current.CancellationToken);
        var filteredOnRejected = await member.FindFacesBySuggestionAsync(topSuggestionPerformerIds: [rejectedPerformer.Id], label: label, cancellationToken: TestContext.Current.CancellationToken);
        var otherUserListed = await AsUser().FindFacesAsync(label: label, cancellationToken: TestContext.Current.CancellationToken);
        var otherUserFilteredOnRejected = await AsUser().FindFacesBySuggestionAsync(topSuggestionPerformerIds: [rejectedPerformer.Id], label: label, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var listedFace = listed.Items.Should().ContainSingle(item => item.Id == face.Id).Subject;
        listedFace.TopSuggestion.Should().NotBeNull();
        listedFace.TopSuggestion!.PerformerId.Should().Be(nextBest.Id);
        listedFace.TopSuggestion.Confidence.Should().Be(0.324f);
        detail.TopSuggestion.Should().NotBeNull();
        detail.TopSuggestion!.PerformerId.Should().Be(nextBest.Id);
        live.Select(item => item.PerformerId).Should().NotContain(rejectedPerformer.Id);
        filteredOnRejected.Items.Should().NotContain(item => item.Id == face.Id);

        // The stored projection stays global: a user who never rejected it still sees the cached top and
        // still matches the suggested-performer filter, so the exclusion is scoped to the deciding user.
        otherUserListed.Items.Should().ContainSingle(item => item.Id == face.Id)
            .Which.TopSuggestion!.PerformerId.Should().Be(rejectedPerformer.Id);
        otherUserFilteredOnRejected.Items.Should().ContainSingle(item => item.Id == face.Id);
    }

    private static FaceSuggestionDto Suggest(
        PerformerDto performer,
        float confidence,
        int evidenceCount = 0,
        string? conflictGroupId = null)
        => new(
            performer.Id,
            performer.Name,
            CoverImageUrl: null,
            Confidence: confidence,
            Why: "api-test-provider",
            Evidence: Enumerable.Range(0, evidenceCount)
                .Select(index => new FaceSuggestionEvidenceDto(index + 1, null, confidence))
                .ToList(),
            ConflictGroupId: conflictGroupId);
}
