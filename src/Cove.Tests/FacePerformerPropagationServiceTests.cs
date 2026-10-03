using System.Data.Common;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

public sealed class FacePerformerPropagationServiceTests
{
    private const string AssignmentOwner = "cove.face-performer-propagation";

    [Fact]
    public async Task Link_AddsPerformerToEveryHostAndRecordsOwnership()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var video = new Video { Title = "Clip" };
        var images = Enumerable.Range(0, 3).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        var face = new Face { Label = "Alex Face", PrimarySourceKey = "face-1" };
        context.AddRange(performer, video, face);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);

        AddAppearance(context, face.Id, FaceAppearanceHostType.Video, video.Id, "ext:ai.faces", "run-1", 0.9f);
        foreach (var image in images)
            AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id, null, null, 0.8f);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        await context.SaveChangesAsync(ct);

        Assert.True(await context.Set<VideoPerformer>().AnyAsync(item => item.VideoId == video.Id && item.PerformerId == performer.Id, ct));
        Assert.Equal(3, await context.Set<ImagePerformer>().CountAsync(item => item.PerformerId == performer.Id, ct));

        var keys = await AssignmentKeysAsync(context, ct);
        Assert.Contains($"performer-assignment:{face.Id}:{performer.Id}:video:{video.Id}", keys);
        foreach (var image in images)
            Assert.Contains($"performer-assignment:{face.Id}:{performer.Id}:image:{image.Id}", keys);

        var videoProvenance = await PerformerProvenanceAsync(context, AffinityHostType.Video, video.Id, ct);
        var videoRow = Assert.Single(videoProvenance);
        Assert.Equal("ext:ai.faces", videoRow.SourceKey);
        Assert.Equal("run-1", videoRow.SourceRunId);
        Assert.Equal(0.9f, videoRow.Confidence);
        Assert.Equal(["Alex"], Names(videoRow));

        var imageRow = Assert.Single(await PerformerProvenanceAsync(context, AffinityHostType.Image, images[0].Id, ct));
        Assert.Equal("face-1", imageRow.SourceKey);
        Assert.Equal(["Alex"], Names(imageRow));
    }

    [Fact]
    public async Task Link_DoesNotAdoptPerformerPlacedByOtherMeans()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var manualImage = new Image { Title = "Manual" };
        var freshImage = new Image { Title = "Fresh" };
        var face = new Face { Label = "Alex Face" };
        context.AddRange(performer, manualImage, freshImage, face);
        await context.SaveChangesAsync(ct);

        context.Set<ImagePerformer>().Add(new ImagePerformer { ImageId = manualImage.Id, PerformerId = performer.Id });
        AddAppearance(context, face.Id, FaceAppearanceHostType.Image, manualImage.Id);
        AddAppearance(context, face.Id, FaceAppearanceHostType.Image, freshImage.Id);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        await context.SaveChangesAsync(ct);

        var keys = await AssignmentKeysAsync(context, ct);
        Assert.Equal([$"performer-assignment:{face.Id}:{performer.Id}:image:{freshImage.Id}"], keys);
        Assert.Empty(await PerformerProvenanceAsync(context, AffinityHostType.Image, manualImage.Id, ct));

        await service.ApplyLinkChangeAsync(face.Id, performer.Id, null, ct);
        await context.SaveChangesAsync(ct);

        // Unlinking removes only what propagation added; the manual link stays.
        Assert.True(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == manualImage.Id && item.PerformerId == performer.Id, ct));
        Assert.False(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == freshImage.Id, ct));
        Assert.Empty(await AssignmentKeysAsync(context, ct));
    }

    [Fact]
    public async Task Link_SharesOwnershipWithAnotherFaceAlreadyPropagatingThePerformer()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var other = new Performer { Name = "Blair" };
        var image = new Image { Title = "Shared" };
        var firstFace = new Face { Label = "First" };
        var secondFace = new Face { Label = "Second" };
        var otherFace = new Face { Label = "Other" };
        context.AddRange(performer, other, image, firstFace, secondFace, otherFace);
        await context.SaveChangesAsync(ct);

        AddAppearance(context, firstFace.Id, FaceAppearanceHostType.Image, image.Id);
        AddAppearance(context, secondFace.Id, FaceAppearanceHostType.Image, image.Id);
        AddAppearance(context, otherFace.Id, FaceAppearanceHostType.Image, image.Id);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(otherFace.Id, null, other.Id, ct);
        await service.ApplyLinkChangeAsync(firstFace.Id, null, performer.Id, ct);
        await context.SaveChangesAsync(ct);

        await service.ApplyLinkChangeAsync(secondFace.Id, null, performer.Id, ct);
        await context.SaveChangesAsync(ct);

        var keys = await AssignmentKeysAsync(context, ct);
        Assert.Contains($"performer-assignment:{secondFace.Id}:{performer.Id}:image:{image.Id}", keys);
        Assert.Equal(["Alex", "Blair"], Names(Assert.Single(await PerformerProvenanceAsync(context, AffinityHostType.Image, image.Id, ct))));

        // The first face's assignment still keeps the performer on the image after the second face unlinks.
        await service.ApplyLinkChangeAsync(secondFace.Id, performer.Id, null, ct);
        await context.SaveChangesAsync(ct);
        Assert.True(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == image.Id && item.PerformerId == performer.Id, ct));

        await service.ApplyLinkChangeAsync(firstFace.Id, performer.Id, null, ct);
        await context.SaveChangesAsync(ct);
        Assert.False(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == image.Id && item.PerformerId == performer.Id, ct));
        Assert.True(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == image.Id && item.PerformerId == other.Id, ct));
        Assert.Equal([$"performer-assignment:{otherFace.Id}:{other.Id}:image:{image.Id}"], await AssignmentKeysAsync(context, ct));

        var rows = await PerformerProvenanceAsync(context, AffinityHostType.Image, image.Id, ct);
        var unlinkRow = Assert.Single(rows, row => row.SourceKey == "face-performer-propagation");
        Assert.Equal(["Blair"], Names(unlinkRow));
    }

    [Fact]
    public async Task Relink_MovesHostsFromOldPerformerToNewPerformerInOneUnitOfWork()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var oldPerformer = new Performer { Name = "Old" };
        var newPerformer = new Performer { Name = "New" };
        var video = new Video { Title = "Clip" };
        var image = new Image { Title = "Image" };
        var face = new Face { Label = "Face", PrimarySourceKey = "face-9" };
        context.AddRange(oldPerformer, newPerformer, video, image, face);
        await context.SaveChangesAsync(ct);

        AddAppearance(context, face.Id, FaceAppearanceHostType.Video, video.Id);
        AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, oldPerformer.Id, ct);
        await context.SaveChangesAsync(ct);

        await service.ApplyLinkChangeAsync(face.Id, oldPerformer.Id, newPerformer.Id, ct);
        await context.SaveChangesAsync(ct);

        Assert.Equal([newPerformer.Id], await context.Set<VideoPerformer>().Where(item => item.VideoId == video.Id).Select(item => item.PerformerId).ToListAsync(ct));
        Assert.Equal([newPerformer.Id], await context.Set<ImagePerformer>().Where(item => item.ImageId == image.Id).Select(item => item.PerformerId).ToListAsync(ct));
        Assert.Equal(
            [
                $"performer-assignment:{face.Id}:{newPerformer.Id}:image:{image.Id}",
                $"performer-assignment:{face.Id}:{newPerformer.Id}:video:{video.Id}",
            ],
            await AssignmentKeysAsync(context, ct));

        var rows = await PerformerProvenanceAsync(context, AffinityHostType.Video, video.Id, ct);
        Assert.Equal(["New"], Names(Assert.Single(rows, row => row.SourceKey != "face-performer-propagation")));
        Assert.Empty(Names(Assert.Single(rows, row => row.SourceKey == "face-performer-propagation")));
    }

    [Fact]
    public async Task Relink_WithoutSourceKeysRecordsOneProvenanceRowForTheNewPerformer()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var oldPerformer = new Performer { Name = "Old" };
        var newPerformer = new Performer { Name = "New" };
        var image = new Image { Title = "Image" };
        var face = new Face { Label = "Face" };
        context.AddRange(oldPerformer, newPerformer, image, face);
        await context.SaveChangesAsync(ct);
        AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, oldPerformer.Id, ct);
        await context.SaveChangesAsync(ct);

        // Removal and addition both resolve to the propagation source key, so they target the same row.
        await service.ApplyLinkChangeAsync(face.Id, oldPerformer.Id, newPerformer.Id, ct);
        await context.SaveChangesAsync(ct);

        var row = Assert.Single(await PerformerProvenanceAsync(context, AffinityHostType.Image, image.Id, ct));
        Assert.Equal("face-performer-propagation", row.SourceKey);
        Assert.Equal(["New"], Names(row));
    }

    [Fact]
    public async Task LinkChanges_WithinOneUnitOfWorkKeepHostLinksConsistent()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var first = new Performer { Name = "First" };
        var second = new Performer { Name = "Second" };
        var image = new Image { Title = "Shared" };
        var faceA = new Face { Label = "A" };
        var faceB = new Face { Label = "B" };
        context.AddRange(first, second, image, faceA, faceB);
        await context.SaveChangesAsync(ct);
        AddAppearance(context, faceA.Id, FaceAppearanceHostType.Image, image.Id);
        AddAppearance(context, faceB.Id, FaceAppearanceHostType.Image, image.Id);
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));

        // Two faces gaining the same performer on a shared host in one save add the link once.
        await service.ApplyLinkChangeAsync(faceA.Id, null, first.Id, ct);
        await service.ApplyLinkChangeAsync(faceB.Id, null, first.Id, ct);
        await context.SaveChangesAsync(ct);
        Assert.Equal([first.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal(2, (await AssignmentKeysAsync(context, ct)).Count);

        // Releasing the performer and claiming it back before saving keeps the link and its ownership.
        await service.ApplyLinkChangeAsync(faceA.Id, first.Id, null, ct);
        await service.ApplyLinkChangeAsync(faceB.Id, first.Id, null, ct);
        await service.ApplyLinkChangeAsync(faceB.Id, null, first.Id, ct);
        await context.SaveChangesAsync(ct);
        Assert.Equal([first.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal([$"performer-assignment:{faceB.Id}:{first.Id}:image:{image.Id}"], await AssignmentKeysAsync(context, ct));

        // Linking and relinking before saving leaves no unowned link behind.
        await service.ApplyLinkChangeAsync(faceA.Id, null, second.Id, ct);
        await service.ApplyLinkChangeAsync(faceA.Id, second.Id, null, ct);
        await context.SaveChangesAsync(ct);
        Assert.Equal([first.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal([$"performer-assignment:{faceB.Id}:{first.Id}:image:{image.Id}"], await AssignmentKeysAsync(context, ct));
    }

    [Fact]
    public async Task ReconcileHost_AppliesLinkedFacesAndDropsFacesNoLongerOnTheHost()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var kept = new Performer { Name = "Kept" };
        var dropped = new Performer { Name = "Dropped" };
        var manual = new Performer { Name = "Manual" };
        var image = new Image { Title = "Image" };
        var keptFace = new Face { Label = "Kept" };
        var droppedFace = new Face { Label = "Dropped" };
        var manualFace = new Face { Label = "Manual" };
        context.AddRange(kept, dropped, manual, image, keptFace, droppedFace, manualFace);
        await context.SaveChangesAsync(ct);

        context.Set<ImagePerformer>().Add(new ImagePerformer { ImageId = image.Id, PerformerId = manual.Id });
        AddAppearance(context, droppedFace.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(droppedFace.Id, null, dropped.Id, ct);
        await context.SaveChangesAsync(ct);

        // Re-processing replaces the dropped face with two linked faces, one matching a manual performer.
        keptFace.PerformerId = kept.Id;
        manualFace.PerformerId = manual.Id;
        context.FaceAppearances.RemoveRange(await context.FaceAppearances.Where(item => item.FaceId == droppedFace.Id).ToListAsync(ct));
        AddAppearance(context, keptFace.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        AddAppearance(context, manualFace.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        await context.SaveChangesAsync(ct);

        await service.ReconcileHostAsync(FaceAppearanceHostType.Image, image.Id, ct);
        await context.SaveChangesAsync(ct);

        Assert.Equal([kept.Id, manual.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal([$"performer-assignment:{keptFace.Id}:{kept.Id}:image:{image.Id}"], await AssignmentKeysAsync(context, ct));
        var row = Assert.Single(await PerformerProvenanceAsync(context, AffinityHostType.Image, image.Id, ct), item => item.SourceKey == "ext:ai.faces");
        Assert.Equal(["Kept"], Names(row));
    }

    [Fact]
    public async Task ReconcileHost_AcrossSeveralHostsInOneUnitOfWork_AppliesThePerformerToEach()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var images = Enumerable.Range(0, 3).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        var face = new Face { Label = "Face" };
        context.AddRange(performer, face);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);
        foreach (var image in images)
            AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        face.PerformerId = performer.Id;
        await context.SaveChangesAsync(ct);

        // Each host's reconcile stages a link; a later host must not mistake an earlier host's pending link
        // for its own.
        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        foreach (var image in images)
            await service.ReconcileHostAsync(FaceAppearanceHostType.Image, image.Id, ct);
        await context.SaveChangesAsync(ct);

        foreach (var image in images)
            Assert.Equal([performer.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal(
            images.Select(image => $"performer-assignment:{face.Id}:{performer.Id}:image:{image.Id}").Order(StringComparer.Ordinal),
            await AssignmentKeysAsync(context, ct));
    }

    [Fact]
    public async Task ReconcileHost_AfterReplacingTheHostsLinkCollection_KeepsTheReplacedLink()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var image = new Image { Title = "Image" };
        var face = new Face { Label = "Face" };
        context.AddRange(performer, image, face);
        await context.SaveChangesAsync(ct);
        AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        face.PerformerId = performer.Id;
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        // Replacing the whole collection tracks the old link as Deleted and an equal new link as Added.
        var tracked = await context.Images.Include(item => item.ImagePerformers).SingleAsync(item => item.Id == image.Id, ct);
        tracked.ImagePerformers = [new ImagePerformer { ImageId = image.Id, PerformerId = performer.Id }];
        context.ChangeTracker.DetectChanges();

        await service.ReconcileHostAsync(FaceAppearanceHostType.Image, image.Id, ct);
        await context.SaveChangesAsync(ct);

        Assert.Equal([performer.Id], await ImagePerformerIdsAsync(context, image.Id, ct));
        Assert.Equal([$"performer-assignment:{face.Id}:{performer.Id}:image:{image.Id}"], await AssignmentKeysAsync(context, ct));
    }

    [Fact]
    public async Task ReconcileHosts_ChecksAFixedNumberOfQueriesRegardlessOfHostCount()
    {
        var small = await CountReconcileQueriesAsync(hostCount: 4);
        var large = await CountReconcileQueriesAsync(hostCount: 120);

        Assert.Equal(small, large);
    }

    private static async Task<int> CountReconcileQueriesAsync(int hostCount)
    {
        var counter = new CommandCounter();
        await using var scope = await CreateContextAsync(counter);
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var other = new Performer { Name = "Blair" };
        var images = Enumerable.Range(0, hostCount).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        var linkedFace = new Face { Label = "Linked" };
        var droppedFace = new Face { Label = "Dropped" };
        context.AddRange(performer, other, linkedFace, droppedFace);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);
        foreach (var image in images)
        {
            AddAppearance(context, linkedFace.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
            AddAppearance(context, droppedFace.Id, FaceAppearanceHostType.Image, image.Id, "ext:ai.faces");
        }
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(droppedFace.Id, null, other.Id, ct);
        droppedFace.PerformerId = other.Id;
        await context.SaveChangesAsync(ct);

        // Re-processing drops one face from every host and links the other.
        context.FaceAppearances.RemoveRange(await context.FaceAppearances.Where(item => item.FaceId == droppedFace.Id).ToListAsync(ct));
        linkedFace.PerformerId = performer.Id;
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        counter.Count = 0;
        await service.ReconcileHostsUnscopedAsync(images.Select(image => (FaceAppearanceHostType.Image, image.Id)), ct);
        var queries = counter.Count;
        await context.SaveChangesAsync(ct);

        Assert.Equal(hostCount, await context.Set<ImagePerformer>().CountAsync(item => item.PerformerId == performer.Id, ct));
        Assert.Equal(0, await context.Set<ImagePerformer>().CountAsync(item => item.PerformerId == other.Id, ct));
        Assert.Equal(hostCount, (await AssignmentKeysAsync(context, ct)).Count);
        return queries;
    }

    [Fact]
    public async Task Link_WritesEachImageOnceWithItsPerformerCount()
    {
        var counter = new CommandCounter { Filter = "UPDATE \"images\"" };
        await using var scope = await CreateContextAsync(counter);
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var images = Enumerable.Range(0, 3).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        var face = new Face { Label = "Face" };
        context.AddRange(performer, face);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);
        foreach (var image in images)
            AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        counter.Count = 0;
        await context.SaveChangesAsync(ct);

        Assert.Equal(images.Length, counter.Count);
        context.ChangeTracker.Clear();
        Assert.All(await context.Images.AsNoTracking().ToListAsync(ct), image =>
        {
            Assert.Equal([performer.Id], image.PerformerIds);
            Assert.Equal(1, image.PerformerCount);
        });
    }

    [Fact]
    public async Task Link_FallsBackToDetectionsWhenFaceHasNoAppearances()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var image = new Image { Title = "Detected" };
        var face = new Face { Label = "Face" };
        context.AddRange(performer, image, face);
        await context.SaveChangesAsync(ct);

        context.Detections.Add(new Detection
        {
            HostType = DetectionHostType.Image,
            HostId = image.Id,
            Class = "face",
            Score = 0.7f,
            RefKind = "face",
            RefId = face.Id,
            SourceKey = "ext:ai.faces",
        });
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        await context.SaveChangesAsync(ct);

        Assert.True(await context.Set<ImagePerformer>().AnyAsync(item => item.ImageId == image.Id && item.PerformerId == performer.Id, ct));
    }

    [Fact]
    public async Task LinkAndUnlink_IssueAFixedNumberOfQueriesRegardlessOfHostCount()
    {
        var small = await CountLinkAndUnlinkQueriesAsync(hostCount: 4);
        var large = await CountLinkAndUnlinkQueriesAsync(hostCount: 120);

        Assert.Equal(small.Link, large.Link);
        Assert.Equal(small.Unlink, large.Unlink);
    }

    private static async Task<(int Link, int Unlink)> CountLinkAndUnlinkQueriesAsync(int hostCount)
    {
        var counter = new CommandCounter();
        await using var scope = await CreateContextAsync(counter);
        var context = scope.Context;
        var ct = TestContext.Current.CancellationToken;

        var performer = new Performer { Name = "Alex" };
        var manual = new Performer { Name = "Manual" };
        var otherFace = new Face { Label = "Other" };
        var face = new Face { Label = "Face", PrimarySourceKey = "face-1" };
        var images = Enumerable.Range(0, hostCount).Select(index => new Image { Title = $"Image {index}" }).ToArray();
        var video = new Video { Title = "Clip" };
        context.AddRange(performer, manual, otherFace, face, video);
        context.AddRange(images);
        await context.SaveChangesAsync(ct);

        AddAppearance(context, face.Id, FaceAppearanceHostType.Video, video.Id);
        foreach (var image in images)
        {
            AddAppearance(context, face.Id, FaceAppearanceHostType.Image, image.Id);
            AddAppearance(context, otherFace.Id, FaceAppearanceHostType.Image, image.Id);
        }
        // Mix in hosts where the performer is already present: manually, and through another face.
        context.Set<ImagePerformer>().Add(new ImagePerformer { ImageId = images[0].Id, PerformerId = performer.Id });
        await context.SaveChangesAsync(ct);

        var service = new FacePerformerPropagationService(context, new FieldProvenanceService(context));
        await service.ApplyLinkChangeAsync(otherFace.Id, null, manual.Id, ct);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        counter.Count = 0;
        await service.ApplyLinkChangeAsync(face.Id, null, performer.Id, ct);
        var link = counter.Count;
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        counter.Count = 0;
        await service.ApplyLinkChangeAsync(face.Id, performer.Id, null, ct);
        var unlink = counter.Count;
        await context.SaveChangesAsync(ct);

        Assert.Equal(hostCount, await context.Set<ImagePerformer>().CountAsync(item => item.PerformerId == manual.Id, ct));
        Assert.Equal([images[0].Id], await context.Set<ImagePerformer>().Where(item => item.PerformerId == performer.Id).Select(item => item.ImageId).ToListAsync(ct));
        return (link, unlink);
    }

    private static void AddAppearance(
        CoveContext context,
        int faceId,
        FaceAppearanceHostType hostType,
        int hostId,
        string? sourceKey = null,
        string? sourceRunId = null,
        float? confidence = null)
        => context.FaceAppearances.Add(new FaceAppearance
        {
            FaceId = faceId,
            HostType = hostType,
            HostId = hostId,
            SourceKey = sourceKey ?? string.Empty,
            SourceRunId = sourceRunId,
            TopConfidence = confidence,
        });

    private static async Task<List<string>> AssignmentKeysAsync(CoveContext context, CancellationToken ct)
        => (await context.ExtensionData
                .AsNoTracking()
                .Where(item => item.ExtensionId == AssignmentOwner)
                .Select(item => item.Key)
                .ToListAsync(ct))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static Task<List<FieldProvenance>> PerformerProvenanceAsync(CoveContext context, AffinityHostType hostType, int hostId, CancellationToken ct)
        => context.FieldProvenance
            .AsNoTracking()
            .Where(item => item.HostType == hostType && item.HostId == hostId && item.FieldKey == "performers")
            .ToListAsync(ct);

    private static string[] Names(FieldProvenance row)
        => JsonSerializer.Deserialize<string[]>(row.ValueJson ?? "[]") ?? [];

    private static async Task<TestContextScope> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new CoveContext(builder.Options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection);
    }

    private static async Task<List<int>> ImagePerformerIdsAsync(CoveContext context, int imageId, CancellationToken ct)
        => await context.Set<ImagePerformer>()
            .AsNoTracking()
            .Where(item => item.ImageId == imageId)
            .Select(item => item.PerformerId)
            .OrderBy(id => id)
            .ToListAsync(ct);

    /// <summary>Counts executed commands, or with <see cref="Filter"/>, occurrences of that text across commands.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public string? Filter { get; init; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            if (Filter is null)
            {
                Count++;
                return;
            }

            var text = command.CommandText;
            for (var index = text.IndexOf(Filter, StringComparison.Ordinal); index >= 0; index = text.IndexOf(Filter, index + Filter.Length, StringComparison.Ordinal))
                Count++;
        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
