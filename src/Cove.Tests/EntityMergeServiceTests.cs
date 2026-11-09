using System.Text.Json;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class EntityMergeServiceTests
{
    [Fact]
    public async Task PerformerMerge_TransfersTypedContextAndFacePropagationReferences()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Performer { Name = "Performer" };
        var source = new Performer { Name = "Source performer" };
        var tag = new Tag { Name = "Occurrence" };
        var role = new Role { Name = "Entity merge fixture" };
        var group = new Group { Name = "Legacy group item" };
        var performerReference = new CustomFieldDefinition
        {
            Key = "related-performers",
            Label = "Related performers",
            Type = CustomFieldTypes.Performer,
            EntityTypes = [CustomFieldEntityTypes.Video],
            IsMultiValue = true,
        };
        db.AddRange(target, source, tag, role, group, performerReference);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var targetAssignmentKey = FacePerformerAssignmentData.BuildKey(
            new FacePerformerAssignmentData.Assignment(71, target.Id, "video", 81));
        var sourceAssignmentKey = FacePerformerAssignmentData.BuildKey(
            new FacePerformerAssignmentData.Assignment(71, source.Id, "video", 81));
        db.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = 91,
                ContextType = "performer",
                ContextId = target.Id,
                TagId = tag.Id,
                SourceKey = "fixture",
                SourceRunId = "run",
                ModelKey = "model",
                Confidence = 0.2f,
            },
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = 91,
                ContextType = "performer",
                ContextId = source.Id,
                TagId = tag.Id,
                SourceKey = "fixture",
                SourceRunId = "run",
                ModelKey = "model",
                Confidence = 0.8f,
                TotalDurationSec = 12,
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 101,
                StartSec = 1,
                Kind = "performer",
                RefId = source.Id,
            },
            new Detection
            {
                HostType = DetectionHostType.Image,
                HostId = 111,
                FrameWidth = 100,
                FrameHeight = 100,
                Class = "person",
                RefKind = "performer",
                RefId = source.Id,
            },
            new Face
            {
                Label = "Unlinked face",
                TopSuggestionPerformerId = source.Id,
                TopSuggestionLocalPerformerId = source.Id,
                TopSuggestionPerformerName = source.Name,
                TopSuggestionConfidence = 97,
                TopSuggestionCoverImageUrl = "https://source.invalid/cover.jpg",
                TopSuggestionExternalUrl = "https://source.invalid/performer",
                TopSuggestionLocalPerformerHasImage = true,
                TopSuggestionLocalPerformerIsLocalOnly = true,
                TopSuggestionComputedAt = DateTime.UtcNow,
            },
            new GroupItem
            {
                GroupId = group.Id,
                Kind = GroupItemKind.Performer,
                HostType = "legacy",
                HostId = source.Id,
            },
            new CustomFieldValue
            {
                DefinitionId = performerReference.Id,
                EntityType = CustomFieldEntityTypes.Video,
                EntityId = 141,
                Position = 0,
                IntegerValue = target.Id,
            },
            new CustomFieldValue
            {
                DefinitionId = performerReference.Id,
                EntityType = CustomFieldEntityTypes.Video,
                EntityId = 141,
                Position = 1,
                IntegerValue = source.Id,
            },
            new CustomFieldValue
            {
                DefinitionId = performerReference.Id,
                EntityType = CustomFieldEntityTypes.Video,
                EntityId = 141,
                Position = 2,
                IntegerValue = 999,
            },
            new ScrapeAttempt
            {
                ScraperId = "fixture",
                EntityType = NameConflictEntityTypes.Performer,
                EntityId = source.Id,
                InputKind = "fragment",
                InputJson = JsonSerializer.Serialize(new { performerId = source.Id }),
                ResultJson = JsonSerializer.Serialize(new { localPerformerId = source.Id }),
            },
            new Embedding
            {
                HostType = EmbeddingHostType.Performer,
                HostId = source.Id,
                Kind = "fixture",
                Modality = EmbeddingModality.Face,
                Dim = 1,
                Vector = new Vector(new float[] { 0.5f }),
                SourceKey = "fixture",
                Meta = JsonDocument.Parse(JsonSerializer.Serialize(new { performerId = source.Id })),
            },
            new AiRun
            {
                RunKey = $"fixture-{Guid.NewGuid():N}",
                SourceKey = "fixture",
                TargetType = AiRunTargetType.Performer,
                TargetId = source.Id,
                Request = JsonDocument.Parse(JsonSerializer.Serialize(new { performerId = source.Id })),
            },
            new ExtensionData
            {
                ExtensionId = FacePerformerAssignmentData.ExtensionId,
                Key = targetAssignmentKey,
                Value = JsonSerializer.Serialize(new { faceId = 71, performerId = target.Id, hostType = "video", hostId = 81 }),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
            new ExtensionData
            {
                ExtensionId = FacePerformerAssignmentData.ExtensionId,
                Key = sourceAssignmentKey,
                Value = JsonSerializer.Serialize(new { faceId = 71, performerId = source.Id, hostType = "video", hostId = 81 }),
                UpdatedAt = DateTime.UtcNow,
            },
            new RoleContentRule
            {
                RoleId = role.Id,
                EntityKind = EntityKinds.Performer,
                ScopeKind = "expression",
                ScopeValue = JsonSerializer.Serialize(new
                {
                    @operator = "and",
                    rules = new object[]
                    {
                        new
                        {
                            scopeKind = "attribute",
                            scopeValue = new { path = "id", @in = new[] { target.Id, source.Id } },
                        },
                        new
                        {
                            scopeKind = "expression",
                            scopeValue = new
                            {
                                @operator = "not",
                                rule = new
                                {
                                    scopeKind = "attribute",
                                    scopeValue = new { path = "id", equals = source.Id },
                                },
                            },
                        },
                    },
                }),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new PerformerMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        Assert.False(await db.Performers.AnyAsync(performer => performer.Id == source.Id, cancellationToken: TestContext.Current.CancellationToken));
        var application = Assert.Single(await db.TagApplications.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(target.Id, application.ContextId);
        Assert.Equal(0.8f, application.Confidence);
        Assert.Equal(12d, application.TotalDurationSec);
        Assert.Equal((long)target.Id, (await db.Segments.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RefId);
        Assert.Equal((long)target.Id, (await db.Detections.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RefId);
        var face = await db.Faces.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(face.TopSuggestionPerformerId);
        Assert.Null(face.TopSuggestionLocalPerformerId);
        Assert.Null(face.TopSuggestionPerformerName);
        Assert.Null(face.TopSuggestionConfidence);
        Assert.Null(face.TopSuggestionCoverImageUrl);
        Assert.Null(face.TopSuggestionExternalUrl);
        Assert.False(face.TopSuggestionLocalPerformerHasImage);
        Assert.False(face.TopSuggestionLocalPerformerIsLocalOnly);
        Assert.Null(face.TopSuggestionComputedAt);
        var groupItem = await db.GroupItems.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, groupItem.HostId);
        Assert.Equal("performer", groupItem.HostType);
        Assert.Equal(GroupItemKind.Performer, groupItem.Kind);
        var customReferences = await db.CustomFieldValues
            .Where(value => value.DefinitionId == performerReference.Id)
            .OrderBy(value => value.Position)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([target.Id, 999], customReferences.Select(value => value.IntegerValue!.Value).ToArray());
        Assert.Equal([0, 1], customReferences.Select(value => value.Position).ToArray());
        var scrapeAttempt = await db.ScrapeAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, scrapeAttempt.EntityId);
        using (var input = JsonDocument.Parse(scrapeAttempt.InputJson))
            Assert.Equal(source.Id, input.RootElement.GetProperty("performerId").GetInt32());
        using (var result = JsonDocument.Parse(scrapeAttempt.ResultJson!))
            Assert.Equal(source.Id, result.RootElement.GetProperty("localPerformerId").GetInt32());
        var embedding = await db.Embeddings.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, embedding.HostId);
        Assert.Equal(source.Id, embedding.Meta!.RootElement.GetProperty("performerId").GetInt32());
        var aiRun = await db.AiRuns.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, aiRun.TargetId);
        Assert.Equal(source.Id, aiRun.Request!.RootElement.GetProperty("performerId").GetInt32());
        var assignment = Assert.Single(await db.ExtensionData
            .Where(row => row.ExtensionId == FacePerformerAssignmentData.ExtensionId)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(targetAssignmentKey, assignment.Key);
        using var assignmentValue = JsonDocument.Parse(assignment.Value);
        Assert.Equal(target.Id, assignmentValue.RootElement.GetProperty("performerId").GetInt32());
        using var contentRule = JsonDocument.Parse((await db.RoleContentRules.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ScopeValue);
        var expressionRules = contentRule.RootElement.GetProperty("rules");
        Assert.Equal(
            [target.Id],
            expressionRules[0].GetProperty("scopeValue").GetProperty("in")
                .EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(
            target.Id,
            expressionRules[1].GetProperty("scopeValue").GetProperty("rule")
                .GetProperty("scopeValue").GetProperty("equals").GetInt32());
    }

    [Fact]
    public async Task PerformerMerge_PreservesTargetIdentityInsteadOfFillingDisambiguation()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Performer { Name = "Shared name" };
        var source = new Performer { Name = "Different name", Disambiguation = "2020" };
        var existingIdentity = new Performer { Name = " shared NAME ", Disambiguation = " 2020 " };
        db.Performers.AddRange(target, source, existingIdentity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new PerformerMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        Assert.False(await db.Performers.AnyAsync(performer => performer.Id == source.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null((await db.Performers.SingleAsync(performer => performer.Id == target.Id, cancellationToken: TestContext.Current.CancellationToken)).Disambiguation);
    }

    [Fact]
    public async Task StudioMerge_RewritesParentFiltersAndTypedReferences()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Studio { Name = "Studio" };
        var source = new Studio { Name = "Source studio" };
        db.Studios.AddRange(target, source);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.AddRange(
            new SavedFilter
            {
                Mode = "studios",
                Name = "Children",
                ObjectFilter = JsonSerializer.Serialize(new
                {
                    parentId = source.Id,
                    unrelated = new { parentId = source.Id },
                }),
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 121,
                StartSec = 1,
                Kind = "studio",
                RefId = source.Id,
            },
            new Detection
            {
                HostType = DetectionHostType.Image,
                HostId = 131,
                FrameWidth = 100,
                FrameHeight = 100,
                Class = "logo",
                RefKind = "studio",
                RefId = source.Id,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new StudioMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        Assert.False(await db.Studios.AnyAsync(studio => studio.Id == source.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((long)target.Id, (await db.Segments.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RefId);
        Assert.Equal((long)target.Id, (await db.Detections.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RefId);
        using var filter = JsonDocument.Parse((await db.SavedFilters.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ObjectFilter!);
        Assert.Equal(target.Id, filter.RootElement.GetProperty("parentId").GetInt32());
        Assert.Equal(source.Id, filter.RootElement.GetProperty("unrelated").GetProperty("parentId").GetInt32());
    }

    [Fact]
    public async Task PerformerMerge_TracksOnlyFacePropagationAssignmentsOfMergedPerformers()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Performer { Name = "Target" };
        var source = new Performer { Name = "Source" };
        var unrelated = new Performer { Name = "Unrelated" };
        db.AddRange(target, source, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var sourceAssignmentKey = FacePerformerAssignmentData.BuildKey(
            new FacePerformerAssignmentData.Assignment(71, source.Id, "video", 81));
        // Face and host ids that equal a merged performer id must not be mistaken for its assignments.
        var unrelatedAssignmentKeys = Enumerable.Range(1, 50)
            .Select(hostId => FacePerformerAssignmentData.BuildKey(
                new FacePerformerAssignmentData.Assignment(source.Id, unrelated.Id, "video", hostId)))
            .ToArray();
        db.ExtensionData.Add(new ExtensionData
        {
            ExtensionId = FacePerformerAssignmentData.ExtensionId,
            Key = sourceAssignmentKey,
            Value = JsonSerializer.Serialize(new { faceId = 71, performerId = source.Id, hostType = "video", hostId = 81 }),
            UpdatedAt = DateTime.UtcNow,
        });
        db.ExtensionData.AddRange(unrelatedAssignmentKeys.Select(key => new ExtensionData
        {
            ExtensionId = FacePerformerAssignmentData.ExtensionId,
            Key = key,
            Value = JsonSerializer.Serialize(new { faceId = source.Id, performerId = unrelated.Id }),
            UpdatedAt = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        await new PerformerMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        // Every save in the merge rescans the tracked entities, so loading the library's assignments
        // makes the merge's cost grow with the library instead of with the merged performers.
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<ExtensionData>(),
            entry => unrelatedAssignmentKeys.Contains(entry.Entity.Key));
        var keys = await db.ExtensionData.AsNoTracking()
            .Where(row => row.ExtensionId == FacePerformerAssignmentData.ExtensionId)
            .Select(row => row.Key)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            unrelatedAssignmentKeys
                .Append(FacePerformerAssignmentData.BuildKey(
                    new FacePerformerAssignmentData.Assignment(71, target.Id, "video", 81)))
                .Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PerformerMerge_RewritesStoredJsonWithoutTrackingRowsItLeavesUnchanged()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Performer { Name = "Target" };
        var source = new Performer { Name = "Source" };
        var user = new User { Username = "json-rewrite-fixture", PasswordHash = "fixture" };
        db.AddRange(target, source, user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var userSession = new UserSession { UserId = user.Id };
        db.UserSessions.Add(userSession);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The id's digits also occur inside unrelated numbers, which the text prefilter cannot tell apart.
        var digitsContainingSourceId = int.Parse($"9{source.Id}9");
        var referencingInteraction = new Interaction
        {
            UserId = user.Id,
            HostType = InteractionHostType.Video,
            HostId = 1,
            Kind = InteractionKind.OpenDetail,
            Meta = JsonDocument.Parse(JsonSerializer.Serialize(new { performerId = source.Id })),
        };
        var mentioningInteraction = new Interaction
        {
            UserId = user.Id,
            HostType = InteractionHostType.Video,
            HostId = 2,
            Kind = InteractionKind.OpenDetail,
            Meta = JsonDocument.Parse(JsonSerializer.Serialize(new { page = "performer", elapsedMs = digitsContainingSourceId })),
        };
        var referencingSegment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = 3,
            StartSec = 1,
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                filter = new { performersCriterion = new { value = new[] { source.Id }, modifier = "INCLUDES" } },
            })),
        };
        var unrelatedSegment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = 4,
            StartSec = 1,
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(new { frame = digitsContainingSourceId })),
        };
        // Merging these sessions copies the latest session's context onto the surviving target session
        // before the JSON rewrite, so the rewrite must also reach that pending in-memory edit.
        var targetPlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Performer,
            HostId = target.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10),
        };
        var sourcePlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Performer,
            HostId = source.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-5),
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new { performerId = source.Id })),
        };
        db.AddRange(referencingInteraction, mentioningInteraction, referencingSegment, unrelatedSegment, targetPlayback, sourcePlayback);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        await new PerformerMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(db.ChangeTracker.Entries<Interaction>(), entry => entry.Entity.Id == mentioningInteraction.Id);
        Assert.DoesNotContain(db.ChangeTracker.Entries<Segment>(), entry => entry.Entity.Id == unrelatedSegment.Id);
        db.ChangeTracker.Clear();
        var interactions = await db.Interactions.ToDictionaryAsync(row => row.Id, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, interactions[referencingInteraction.Id].Meta!.RootElement.GetProperty("performerId").GetInt32());
        Assert.Equal(digitsContainingSourceId, interactions[mentioningInteraction.Id].Meta!.RootElement.GetProperty("elapsedMs").GetInt32());
        var segments = await db.Segments.ToDictionaryAsync(row => row.Id, TestContext.Current.CancellationToken);
        Assert.Equal(
            [target.Id],
            segments[referencingSegment.Id].Payload!.RootElement.GetProperty("filter").GetProperty("performersCriterion")
                .GetProperty("value").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(digitsContainingSourceId, segments[unrelatedSegment.Id].Payload!.RootElement.GetProperty("frame").GetInt32());
        var playback = await db.PlaybackSessions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(targetPlayback.Id, playback.Id);
        Assert.Equal(target.Id, playback.Context!.RootElement.GetProperty("performerId").GetInt32());
    }

    [Fact]
    public async Task StudioMerge_RewritesStudioFiltersThatOnlyNameTheEntityOutsideTheirIdProperty()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Studio { Name = "Target" };
        var source = new Studio { Name = "Source" };
        var user = new User { Username = "studio-json-fixture", PasswordHash = "fixture" };
        db.AddRange(target, source, user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var interaction = new Interaction
        {
            UserId = user.Id,
            HostType = InteractionHostType.Video,
            HostId = 1,
            Kind = InteractionKind.SearchQuery,
            Meta = JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "studios", parentId = source.Id })),
        };
        var group = new Group
        {
            Name = "Child studios",
            QueryJson = JsonSerializer.Serialize(new { objectFilters = new { studios = new { parentId = source.Id } } }),
        };
        db.AddRange(interaction, group);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        await new StudioMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var meta = (await db.Interactions.SingleAsync(TestContext.Current.CancellationToken)).Meta!;
        Assert.Equal(target.Id, meta.RootElement.GetProperty("parentId").GetInt32());
        using var query = JsonDocument.Parse((await db.Groups.SingleAsync(TestContext.Current.CancellationToken)).QueryJson!);
        Assert.Equal(
            target.Id,
            query.RootElement.GetProperty("objectFilters").GetProperty("studios").GetProperty("parentId").GetInt32());
    }

    [Fact]
    public async Task StudioMerge_RewritesStudioShareLinksButNotTrackedLinksOfOtherKinds()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var target = new Studio { Name = "Target" };
        var source = new Studio { Name = "Source" };
        db.AddRange(target, source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var studioLink = new ShareLink
        {
            TokenHash = "studio-link",
            EntityKind = EntityKinds.Studio,
            EntityIds = $"[{source.Id}]",
        };
        var videoLink = new ShareLink
        {
            TokenHash = "video-link",
            EntityKind = "video",
            EntityIds = $"[{source.Id}]",
        };
        db.ShareLinks.AddRange(studioLink, videoLink);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        // A caller's context may already track rows of a rewritten type that the merge must not touch.
        var trackedVideoLink = await db.ShareLinks.SingleAsync(link => link.Id == videoLink.Id, TestContext.Current.CancellationToken);

        await new StudioMergeService(db).MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        Assert.Equal($"[{source.Id}]", trackedVideoLink.EntityIds);
        db.ChangeTracker.Clear();
        var links = await db.ShareLinks.ToDictionaryAsync(link => link.Id, TestContext.Current.CancellationToken);
        Assert.Equal($"[{target.Id}]", links[studioLink.Id].EntityIds);
        Assert.Equal($"[{source.Id}]", links[videoLink.Id].EntityIds);
    }
}
