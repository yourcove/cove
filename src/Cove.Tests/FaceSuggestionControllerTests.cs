using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PermissionKeys = Cove.Core.Auth.Permissions;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class FaceSuggestionControllerTests
{
    [Fact]
    public async Task FaceSuggestionsController_ReturnsEmptyListWithoutSuggesters()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        context.Faces.Add(face);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(context, scope.PrincipalAccessor);

        var result = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task FaceSuggestionsAggregator_DedupesAcrossSuggesters()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var performerA = new Performer { Name = "Performer A" };
        var performerB = new Performer { Name = "Performer B" };
        context.Faces.Add(face);
        context.Performers.AddRange(performerA, performerB);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(performerA.Id, performerA.Name, "/covers/a.jpg", 0.61f, "match-a1", [new FaceSuggestionEvidenceDto(11, "/thumbs/11.jpg", 0.61f)]),
                new FaceSuggestionDto(performerB.Id, performerB.Name, "/covers/b.jpg", 0.55f, "match-b1", [new FaceSuggestionEvidenceDto(12, "/thumbs/12.jpg", 0.55f)]),
            ]),
            new StubFaceSuggester([
                new FaceSuggestionDto(performerA.Id, performerA.Name, "/covers/a-2.jpg", 0.87f, "match-a2", [new FaceSuggestionEvidenceDto(13, "/thumbs/13.jpg", 0.87f), new FaceSuggestionEvidenceDto(14, "/thumbs/14.jpg", 0.83f)]),
            ]));

        var result = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(performerA.Id, suggestions[0].PerformerId);
        Assert.Equal(0.87f, suggestions[0].Confidence);
        Assert.Equal("match-a2", suggestions[0].Why);
        Assert.Equal(performerB.Id, suggestions[1].PerformerId);
    }

    [Fact]
    public async Task FaceSuggestionsController_RejectionsAreHonored()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var rejectedPerformer = new Performer { Name = "Rejected Performer" };
        var remainingPerformer = new Performer { Name = "Remaining Performer" };
        context.Faces.Add(face);
        context.Performers.AddRange(rejectedPerformer, remainingPerformer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(rejectedPerformer.Id, rejectedPerformer.Name, null, 0.82f, "reject-me", []),
                new FaceSuggestionDto(remainingPerformer.Id, remainingPerformer.Name, null, 0.58f, "keep-me", []),
            ]));

        var rejectResult = await controller.RecordSuggestionDecision(face.Id, new FaceSuggestionDecisionDto(rejectedPerformer.Id, FaceSuggestionDecisionValues.Reject), CancellationToken.None);
        Assert.IsType<OkObjectResult>(rejectResult.Result);

        var suggestionsResult = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(suggestionsResult.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        var remaining = Assert.Single(suggestions);
        Assert.Equal(remainingPerformer.Id, remaining.PerformerId);

        var persistedDecision = await context.FaceSuggestionDecisions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(face.Id, persistedDecision.FaceId);
        Assert.Equal(rejectedPerformer.Id, persistedDecision.PerformerId);
        Assert.Equal(7, persistedDecision.UserId);
        Assert.Equal(FaceSuggestionDecisionValues.Reject, persistedDecision.Decision);
    }

    [Fact]
    public async Task FaceSuggestionsController_AcceptLinksPerformerAndSuppressesFutureSuggestions()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var performer = new Performer { Name = "Chosen Performer" };
        context.Faces.Add(face);
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SetCurrentUser(scope.PrincipalAccessor, 11, "linker");

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(performer.Id, performer.Name, null, 0.91f, "best-match", []),
            ]));

        var acceptResult = await controller.RecordSuggestionDecision(face.Id, new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Accept), CancellationToken.None);
        Assert.IsType<OkObjectResult>(acceptResult.Result);

        var persistedFace = await context.Faces.SingleAsync(item => item.Id == face.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(performer.Id, persistedFace.PerformerId);

        var suggestionsResult = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(suggestionsResult.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task BatchLinkTopSuggestion_SavesLargeSelectionsInChunksWithinTheHostBudget()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        // Three faces on 1,000 images each: staging the third would pass the 2,500 host budget, so the batch
        // saves the first two before it.
        const int hostsPerFace = 1000;
        var performer = new Performer { Name = "Chosen Performer" };
        var faces = Enumerable.Range(0, 3).Select(index => new Face { Label = $"Face {index}" }).ToArray();
        var images = Enumerable.Range(0, faces.Length * hostsPerFace).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        context.Add(performer);
        context.AddRange(faces);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);
        for (var index = 0; index < images.Length; index++)
        {
            context.FaceAppearances.Add(new FaceAppearance
            {
                FaceId = faces[index / hostsPerFace].Id,
                HostType = FaceAppearanceHostType.Image,
                HostId = images[index].Id,
                SourceKey = "ext:ai.faces",
            });
        }
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var stagedLinksPerSave = new List<int>();
        context.SavingChanges += (_, _) =>
        {
            var staged = context.ChangeTracker.Entries<ImagePerformer>().Count(entry => entry.State == EntityState.Added);
            if (staged > 0)
                stagedLinksPerSave.Add(staged);
        };

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([new FaceSuggestionDto(performer.Id, performer.Name, null, 0.91f, "best-match", [])]));

        var result = await controller.BatchLinkTopSuggestion(new FaceBatchLinkTopSuggestionDto([.. faces.Select(face => face.Id)]), ct);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var outcome = Assert.IsType<FaceBatchOperationResultDto>(ok.Value);
        Assert.Equal(faces.Select(face => face.Id), outcome.Succeeded);
        Assert.Empty(outcome.Failed);
        Assert.Equal([2 * hostsPerFace, hostsPerFace], stagedLinksPerSave);

        // Only the last chunk's links remain tracked; the earlier chunk was saved and released.
        Assert.Equal(hostsPerFace, context.ChangeTracker.Entries<ImagePerformer>().Count());

        context.ChangeTracker.Clear();
        Assert.All(await context.Faces.AsNoTracking().ToListAsync(ct), face => Assert.Equal(performer.Id, face.PerformerId));
        Assert.Equal(images.Length, await context.Set<ImagePerformer>().CountAsync(item => item.PerformerId == performer.Id, ct));
        Assert.Equal(images.Length, await context.ExtensionData.CountAsync(item => item.Key.StartsWith("performer-assignment:"), ct));
    }

    [Fact]
    public async Task BatchLink_LinksReviewedFacesAndRemembersSkippedOnesAsRejected()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var otherPerformer = new Performer { Name = "Other Performer" };
        context.Performers.AddRange(performer, otherPerformer);
        var first = new Face { Label = "First" };
        var second = new Face { Label = "Second" };
        var skippedFace = new Face { Label = "Not them" };
        var linkedElsewhere = new Face { Label = "Linked elsewhere", Performer = otherPerformer };
        var ignoredFace = new Face { Label = "Ignored", Ignored = true };
        context.Faces.AddRange(first, second, skippedFace, linkedElsewhere, ignoredFace);
        await context.SaveChangesAsync(ct);
        // An earlier accept for the skipped face turns into the reject rather than sitting beside it.
        context.FaceSuggestionDecisions.Add(new FaceSuggestionDecision { FaceId = skippedFace.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Accept });
        await context.SaveChangesAsync(ct);
        var mergedFace = new Face { Label = "Merged", MergedIntoFaceId = first.Id };
        context.Faces.Add(mergedFace);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);
        const int missingFaceId = 987_654;

        var result = await controller.BatchLink(
            new FaceBatchLinkDto(performer.Id, [first.Id, second.Id, linkedElsewhere.Id, mergedFace.Id, ignoredFace.Id, missingFaceId], [skippedFace.Id]),
            ct);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var outcome = Assert.IsType<FaceBatchLinkResultDto>(ok.Value);
        Assert.Equal([first.Id, second.Id], outcome.Linked);
        Assert.Equal([skippedFace.Id], outcome.Rejected);
        Assert.Equal([linkedElsewhere.Id, mergedFace.Id, ignoredFace.Id, missingFaceId], outcome.Skipped.Select(item => item.FaceId));
        Assert.Empty(outcome.Failed);

        context.ChangeTracker.Clear();
        var facesById = await context.Faces.AsNoTracking().ToDictionaryAsync(face => face.Id, ct);
        Assert.Equal(performer.Id, facesById[first.Id].PerformerId);
        Assert.Equal(performer.Id, facesById[second.Id].PerformerId);
        Assert.Null(facesById[skippedFace.Id].PerformerId);
        Assert.Equal(otherPerformer.Id, facesById[linkedElsewhere.Id].PerformerId);
        Assert.Null(facesById[mergedFace.Id].PerformerId);
        Assert.Null(facesById[ignoredFace.Id].PerformerId);

        var decision = await context.FaceSuggestionDecisions.AsNoTracking().SingleAsync(ct);
        Assert.Equal((skippedFace.Id, performer.Id, 7, FaceSuggestionDecisionValues.Reject), (decision.FaceId, decision.PerformerId, decision.UserId, decision.Decision));

        // Skipping the same face again keeps the single decision rather than adding a duplicate.
        var again = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [], [skippedFace.Id]), ct);
        Assert.IsType<OkObjectResult>(again.Result);
        Assert.Equal(1, await context.FaceSuggestionDecisions.CountAsync(ct));
    }

    // Every way of linking a face to a performer turns the linking user's earlier reject of that pair into an
    // accept, as accepting the suggestion does. An administrator bypasses the per-user decision filter, so the
    // endpoints' own user check is what keeps other users' rejects.
    [Theory]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("link", false)]
    [InlineData("link", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task LinkingAFaceTheUserHadTurnedDownTurnsThatRejectIntoAnAccept(string route, bool administrator)
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var otherPerformer = new Performer { Name = "Other Performer" };
        var face = new Face { Label = "Turned down by mistake" };
        context.Performers.AddRange(performer, otherPerformer);
        context.Faces.Add(face);
        await context.SaveChangesAsync(ct);
        context.FaceSuggestionDecisions.AddRange(
            new FaceSuggestionDecision { FaceId = face.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
            // Only this user's reject for this performer changes.
            new FaceSuggestionDecision { FaceId = face.Id, PerformerId = otherPerformer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
            new FaceSuggestionDecision { FaceId = face.Id, PerformerId = performer.Id, UserId = 11, Decision = FaceSuggestionDecisionValues.Reject });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester", administrator);
        var controller = CreateController(context, scope.PrincipalAccessor);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        switch (route)
        {
            case "batch":
                // The batch links a face marked as not this performer only when the request overrides that mark.
                var batch = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [], OverrideRejectFaceIds: [face.Id]), ct);
                Assert.Equal([face.Id], Assert.IsType<FaceBatchLinkResultDto>(Assert.IsType<OkObjectResult>(batch.Result).Value).Linked);
                break;
            case "link":
                Assert.IsType<OkObjectResult>((await controller.Link(face.Id, new FaceLinkDto(performer.Id), ct)).Result);
                break;
            default:
                Assert.IsType<OkObjectResult>((await controller.Update(face.Id, new FaceUpdateDto(face.Label, performer.Id, false, null), ct)).Result);
                break;
        }

        context.ChangeTracker.Clear();
        Assert.Equal(performer.Id, (await context.Faces.AsNoTracking().SingleAsync(ct)).PerformerId);
        // Decisions are filtered to the signed-in user, so read every user's rows without one.
        scope.PrincipalAccessor.Set(null);
        var decisions = await context.FaceSuggestionDecisions.AsNoTracking()
            .OrderBy(item => item.UserId).ThenBy(item => item.PerformerId)
            .Select(item => new { item.PerformerId, item.UserId, item.Decision })
            .ToListAsync(ct);
        Assert.Equal(
            [
                new { PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Accept },
                new { PerformerId = otherPerformer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
                new { PerformerId = performer.Id, UserId = 11, Decision = FaceSuggestionDecisionValues.Reject },
            ],
            decisions);
    }

    // The batch reads the user's "not this performer" marks itself, so a client showing a list from before a
    // reject was saved cannot link that face by sending it as an ordinary candidate. An administrator bypasses the
    // per-user decision filter, so another user's reject must still not count as this user's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchLink_LeavesAFaceMarkedAsNotThisPerformerUnlessTheRequestOverridesThatMark(bool administrator)
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var markedFace = new Face { Label = "Marked as someone else" };
        var markedByAnotherUser = new Face { Label = "Marked by another user" };
        context.Performers.Add(performer);
        context.Faces.AddRange(markedFace, markedByAnotherUser);
        await context.SaveChangesAsync(ct);
        context.FaceSuggestionDecisions.AddRange(
            new FaceSuggestionDecision { FaceId = markedFace.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
            new FaceSuggestionDecision { FaceId = markedByAnotherUser.Id, PerformerId = performer.Id, UserId = 11, Decision = FaceSuggestionDecisionValues.Reject });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester", administrator);
        var controller = CreateController(context, scope.PrincipalAccessor);

        var stale = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [markedFace.Id, markedByAnotherUser.Id]), ct);

        var staleOutcome = Assert.IsType<FaceBatchLinkResultDto>(Assert.IsType<OkObjectResult>(stale.Result).Value);
        Assert.Equal([markedByAnotherUser.Id], staleOutcome.Linked);
        var skip = Assert.Single(staleOutcome.Skipped);
        Assert.Equal((markedFace.Id, "Face is marked as not this performer."), (skip.FaceId, skip.Reason));
        context.ChangeTracker.Clear();
        Assert.Null((await context.Faces.AsNoTracking().SingleAsync(face => face.Id == markedFace.Id, ct)).PerformerId);
        Assert.Equal(
            FaceSuggestionDecisionValues.Reject,
            (await context.FaceSuggestionDecisions.AsNoTracking().SingleAsync(item => item.FaceId == markedFace.Id && item.UserId == 7, ct)).Decision);

        var overridden = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [], OverrideRejectFaceIds: [markedFace.Id]), ct);

        Assert.Equal([markedFace.Id], Assert.IsType<FaceBatchLinkResultDto>(Assert.IsType<OkObjectResult>(overridden.Result).Value).Linked);
        context.ChangeTracker.Clear();
        Assert.Equal(performer.Id, (await context.Faces.AsNoTracking().SingleAsync(face => face.Id == markedFace.Id, ct)).PerformerId);
        Assert.Equal(
            FaceSuggestionDecisionValues.Accept,
            (await context.FaceSuggestionDecisions.AsNoTracking().SingleAsync(item => item.FaceId == markedFace.Id && item.UserId == 7, ct)).Decision);
    }

    // Two reviews can run at once, for example one face's review skipping X while another face's review links it. A
    // reject that another request writes while this batch links X was not there when the marks were read, so it
    // is not this batch's to turn into an accept: X ends up linked, but the other review's skip is kept.
    [Fact]
    public async Task BatchLink_KeepsARejectThatAnotherRequestWritesWhileItLinks()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var face = new Face { Label = "Skipped elsewhere meanwhile" };
        context.Performers.Add(performer);
        context.Faces.Add(face);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var written = false;
        context.SavingChanges += (_, _) =>
        {
            if (written || !context.ChangeTracker.Entries<Face>().Any(entry => entry.State == EntityState.Modified))
                return;
            written = true;
            using var other = new CoveContext(scope.Database.CreateOptions(), new CurrentPrincipalAccessor());
            other.FaceSuggestionDecisions.Add(new FaceSuggestionDecision { FaceId = face.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject });
            other.SaveChanges();
        };

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);

        var result = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [face.Id]), ct);

        Assert.True(written);
        Assert.Equal([face.Id], Assert.IsType<FaceBatchLinkResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Linked);
        context.ChangeTracker.Clear();
        Assert.Equal(
            FaceSuggestionDecisionValues.Reject,
            (await context.FaceSuggestionDecisions.AsNoTracking().SingleAsync(item => item.FaceId == face.Id, ct)).Decision);
    }

    // The skips are saved on their own before anything is linked, so another review reading this user's marks
    // while this batch is still linking already sees them.
    [Fact]
    public async Task BatchLink_SavesTheSkipsBeforeLinkingAnyFace()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var toLink = new Face { Label = "To link" };
        var toSkip = new Face { Label = "To skip" };
        context.Performers.Add(performer);
        context.Faces.AddRange(toLink, toSkip);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var saves = new List<(int Decisions, int Links)>();
        context.SavingChanges += (_, _) =>
        {
            var decisions = context.ChangeTracker.Entries<FaceSuggestionDecision>().Count(entry => entry.State is EntityState.Added or EntityState.Modified);
            var links = context.ChangeTracker.Entries<Face>().Count(entry => entry.State == EntityState.Modified);
            if (decisions + links > 0)
                saves.Add((decisions, links));
        };

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);

        var result = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [toLink.Id], [toSkip.Id]), ct);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal([(1, 0), (0, 1)], saves);
    }

    [Fact]
    public async Task BatchLink_TakesAFaceBothListedAndOverriddenAsTheOverrideAndRefusesAnOverriddenSkip()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var face = new Face { Label = "Marked as someone else" };
        context.Performers.Add(performer);
        context.Faces.Add(face);
        await context.SaveChangesAsync(ct);
        context.FaceSuggestionDecisions.Add(new FaceSuggestionDecision { FaceId = face.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);

        // Overriding and skipping the same face contradict each other, so nothing is done.
        var contradictory = await controller.BatchLink(
            new FaceBatchLinkDto(performer.Id, [], RejectFaceIds: [face.Id], OverrideRejectFaceIds: [face.Id]),
            ct);
        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(contradictory.Result).Value);
        context.ChangeTracker.Clear();
        Assert.Null((await context.Faces.AsNoTracking().SingleAsync(ct)).PerformerId);

        // Listed to link and to override, the face is linked once, as the override.
        var both = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [face.Id], OverrideRejectFaceIds: [face.Id]), ct);

        var outcome = Assert.IsType<FaceBatchLinkResultDto>(Assert.IsType<OkObjectResult>(both.Result).Value);
        Assert.Equal([face.Id], outcome.Linked);
        Assert.Empty(outcome.Skipped);
        context.ChangeTracker.Clear();
        Assert.Equal(FaceSuggestionDecisionValues.Accept, (await context.FaceSuggestionDecisions.AsNoTracking().SingleAsync(ct)).Decision);
    }

    [Fact]
    public async Task BatchLink_RejectsAnInvalidRequestWithoutChangingAnything()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var face = new Face { Label = "Face" };
        context.Performers.Add(performer);
        context.Faces.Add(face);
        await context.SaveChangesAsync(ct);

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);

        var overlapping = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [face.Id], [face.Id]), ct);
        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(overlapping.Result).Value);

        var unknownPerformer = await controller.BatchLink(new FaceBatchLinkDto(987_654, [face.Id]), ct);
        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(unknownPerformer.Result).Value);

        var empty = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, []), ct);
        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(empty.Result).Value);

        scope.PrincipalAccessor.Set(null);
        var anonymousSkip = await controller.BatchLink(new FaceBatchLinkDto(performer.Id, [], [face.Id]), ct);
        Assert.IsType<UnauthorizedResult>(anonymousSkip.Result);

        context.ChangeTracker.Clear();
        Assert.Null((await context.Faces.AsNoTracking().SingleAsync(ct)).PerformerId);
        Assert.Empty(await context.FaceSuggestionDecisions.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task GetSimilar_MarksFacesTheUserRejectedForTheSourcePerformer()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Chosen Performer" };
        var otherPerformer = new Performer { Name = "Other Performer" };
        context.Performers.AddRange(performer, otherPerformer);
        var source = new Face { Label = "Source", Performer = performer };
        var rejected = new Face { Label = "Rejected" };
        var rejectedForOther = new Face { Label = "Rejected for another performer" };
        var rejectedByOtherUser = new Face { Label = "Rejected by another user" };
        var candidate = new Face { Label = "Candidate" };
        Face[] faces = [source, rejected, rejectedForOther, rejectedByOtherUser, candidate];
        context.Faces.AddRange(faces);
        await context.SaveChangesAsync(ct);
        for (var index = 0; index < faces.Length; index++)
        {
            context.Embeddings.Add(new Embedding
            {
                HostType = EmbeddingHostType.Face,
                HostId = faces[index].Id,
                Kind = "face.arcface",
                KindFamily = "face.arcface",
                Modality = EmbeddingModality.Face,
                IsSemantic = true,
                Dim = 3,
                Vector = new Pgvector.Vector(new[] { 1f, index * 0.05f, 0f }),
                SourceKey = "ext:ai.faces",
            });
        }
        context.FaceSuggestionDecisions.AddRange(
            new FaceSuggestionDecision { FaceId = rejected.Id, PerformerId = performer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
            new FaceSuggestionDecision { FaceId = rejectedForOther.Id, PerformerId = otherPerformer.Id, UserId = 7, Decision = FaceSuggestionDecisionValues.Reject },
            new FaceSuggestionDecision { FaceId = rejectedByOtherUser.Id, PerformerId = performer.Id, UserId = 11, Decision = FaceSuggestionDecisionValues.Reject });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");
        var controller = CreateController(context, scope.PrincipalAccessor);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        var result = await controller.GetSimilar(source.Id, "face.arcface", null, null, null, null, null, null, null, null, 1, 10, 10, ct);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(ok.Value);
        Assert.Equal(4, page.Items.Count);
        Assert.Equal([rejected.Id], page.Items.Where(item => item.RejectedForSourcePerformer).Select(item => item.Id));

        // The filter applies before paging, so hiding the marked face moves the next candidate onto the first page.
        var hidden = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetSimilar(source.Id, "face.arcface", null, "distance", "asc", null, markedNotPerformer: false, perPage: 1, k: 10, cancellationToken: ct)).Result).Value);
        Assert.Equal(3, hidden.TotalCount);
        Assert.Equal([rejectedForOther.Id], hidden.Items.Select(item => item.Id));
        Assert.DoesNotContain(hidden.Items, item => item.RejectedForSourcePerformer);

        var onlyMarked = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetSimilar(source.Id, "face.arcface", null, null, null, null, markedNotPerformer: true, k: 10, cancellationToken: ct)).Result).Value);
        Assert.Equal([rejected.Id], onlyMarked.Items.Select(item => item.Id));
        Assert.True(onlyMarked.Items.Single().RejectedForSourcePerformer);

        // An administrator bypasses the per-user decision filter, and still sees only their own marks.
        SetCurrentUser(scope.PrincipalAccessor, 7, "tester", administrator: true);
        var adminMarked = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetSimilar(source.Id, "face.arcface", null, null, null, null, markedNotPerformer: true, k: 10, cancellationToken: ct)).Result).Value);
        Assert.Equal([rejected.Id], adminMarked.Items.Select(item => item.Id));

        // An unlinked source face has no performer to be marked against, so nothing is marked.
        var fromUnlinked = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetSimilar(candidate.Id, "face.arcface", null, null, null, null, markedNotPerformer: false, k: 10, cancellationToken: ct)).Result).Value);
        Assert.Equal(4, fromUnlinked.TotalCount);
        Assert.DoesNotContain(fromUnlinked.Items, item => item.RejectedForSourcePerformer);
    }

    private static FacesController CreateController(CoveContext context, CurrentPrincipalAccessor principalAccessor, params IFaceSuggester[] suggesters)
    {
        var embeddingService = new EmbeddingService(context, []);
        return new FacesController(
            context,
            embeddingService,
            new StubBlobService(),
            new FacePerformerPropagationService(context),
            [],
            NullLogger<FacesController>.Instance,
            suggesters,
            principalAccessor);
    }

    private static void SetCurrentUser(CurrentPrincipalAccessor principalAccessor, int userId, string username, bool administrator = false)
    {
        // The endpoints require faces.read and faces.write, and the authorization filters hide faces and
        // performers from a user who cannot read them.
        principalAccessor.Set(new CovePrincipal
        {
            UserId = userId,
            Username = username,
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Permissions = new HashSet<string>(
                administrator
                    ? [PermissionKeys.All]
                    : [PermissionKeys.FacesRead, PermissionKeys.FacesWrite, PermissionKeys.PerformersRead, PermissionKeys.ImagesRead, PermissionKeys.VideosRead],
                StringComparer.OrdinalIgnoreCase),
        });
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await CovePostgresDatabase.CreateAsync(ct);
        var principalAccessor = new CurrentPrincipalAccessor();
        var context = new CoveContext(database.CreateOptions(), principalAccessor);
        var scope = new TestContextScope(context, database, principalAccessor);
        try
        {
            context.Users.AddRange(
                new User { Id = 7, Username = "tester", PasswordHash = "test" },
                new User { Id = 11, Username = "linker", PasswordHash = "test" });
            await context.SaveChangesAsync(ct);
            await database.SyncIdentitySequencesAsync(ct);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class TestContextScope(CoveContext context, CovePostgresDatabase database, CurrentPrincipalAccessor principalAccessor) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;
        public CovePostgresDatabase Database { get; } = database;
        public CurrentPrincipalAccessor PrincipalAccessor { get; } = principalAccessor;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class StubFaceSuggester(IReadOnlyList<FaceSuggestionDto> suggestions) : IFaceSuggester
    {
        public Task<IReadOnlyList<FaceSuggestionDto>> SuggestForAsync(int faceId, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FaceSuggestionDto>>(suggestions.Take(maxResults).ToList());
    }

    private sealed class StubBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
