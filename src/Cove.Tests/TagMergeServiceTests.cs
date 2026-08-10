using System.Text.Json;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class TagMergeServiceTests
{
    [Fact]
    public async Task MergeAsync_StopsBeforeMutationWhenExtensionReferencesCannotBeTransferred()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        db.Tags.AddRange(target, source);
        await db.SaveChangesAsync();

        var inspector = new StubExternalReferenceInspector(
            new Dictionary<int, int> { [source.Id] = 2 });

        var exception = await Assert.ThrowsAsync<TagMergeBlockedException>(
            () => new TagMergeService(db, externalReferenceInspector: inspector)
                .MergeAsync(target.Id, [source.Id]));

        Assert.Equal(2, exception.ReferenceCount);
        Assert.Equal(1, exception.AffectedTagCount);
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == target.Id));
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == source.Id));
    }

    [Fact]
    public async Task MergeAsync_TransfersRelationshipsMetadataAndStoredTagReferences()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target", Description = "Survivor description" };
        var source = new Tag
        {
            Name = " Source ",
            SortName = "Source sort",
            SearchText = "Source supplemental search text",
            Favorite = true,
            Aliases =
            [
                new TagAlias { Alias = " source " },
                new TagAlias { Alias = "Historical alias" },
            ],
            RemoteIds = [new TagRemoteId { Endpoint = "fixture", RemoteId = "remote-source" }],
        };
        var child = new Tag { Name = "Child" };
        db.AddRange(target, source, child);
        var video = new Video { Title = "Merge fixture" };
        db.Videos.Add(video);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var segmentDefaultFilter = JsonSerializer.Serialize(new
        {
            findFilter = new { sort = "title" },
            objectFilter = new
            {
                videoTagsCriterion = new { value = new[] { source.Id } },
                rawTagsCriterion = new { requiredIds = new[] { source.Id, child.Id } },
            },
            uiOptions = new { density = "compact" },
        });
        var tagDefaultFilter = JsonSerializer.Serialize(new
        {
            objectFilter = new { parentsCriterion = new { value = new[] { source.Id } } },
        });
        var user = new User
        {
            Username = "tag-merge-preferences",
            PasswordHash = "fixture",
            UiPreferencesJson = JsonSerializer.Serialize(new
            {
                theme = new { activeThemeId = "dark" },
                defaultFilters = new Dictionary<string, string>
                {
                    ["segments"] = segmentDefaultFilter,
                    ["tags"] = tagDefaultFilter,
                    ["malformed"] = "{",
                },
            }),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Set<VideoTag>().Add(new VideoTag { VideoId = video.Id, TagId = source.Id });
        db.Set<TagParent>().AddRange(
            new TagParent { ParentId = source.Id, ChildId = child.Id },
            new TagParent { ParentId = target.Id, ChildId = source.Id });
        db.Segments.Add(new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            TagId = source.Id,
            Payload = JsonSerializer.SerializeToDocument(new { secondaryTagIds = new[] { source.Id, child.Id } }),
        });
        db.Ratings.AddRange(
            new Rating { UserId = user.Id, HostType = RatingHostType.Tag, HostId = target.Id, Value = 70 },
            new Rating { UserId = user.Id, HostType = RatingHostType.Tag, HostId = source.Id, Value = 90 });
        db.SavedFilters.Add(new SavedFilter
        {
            Mode = "videos",
            Name = "Tag filter fixture",
            ObjectFilter = JsonSerializer.Serialize(new
            {
                tagIds = new[] { source.Id, child.Id },
                tagsCriterion = new { value = new[] { source.Id }, excludes = new[] { source.Id, child.Id } },
                videoTagsCriterion = new { value = new[] { source.Id }, requiredIds = new[] { source.Id, child.Id } },
                rawTagsCriterion = new { excludes = new[] { source.Id } },
            }),
        });
        await db.SaveChangesAsync();

        var result = await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        Assert.Equal(target.Id, result.TargetId);
        Assert.Equal([source.Id], result.MergedSourceIds);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == source.Id));

        var merged = await db.Tags
            .Include(tag => tag.Aliases)
            .Include(tag => tag.RemoteIds)
            .SingleAsync(tag => tag.Id == target.Id);
        Assert.Equal("Survivor description", merged.Description);
        Assert.Equal("Source sort", merged.SortName);
        Assert.Equal("Source supplemental search text", merged.SearchText);
        Assert.True(merged.Favorite);
        Assert.Contains(merged.Aliases, alias => alias.Alias == "Historical alias");
        Assert.DoesNotContain(merged.Aliases, alias => TagNameRules.NamesEqual(alias.Alias, target.Name));
        Assert.Contains(merged.RemoteIds, remote => remote.RemoteId == "remote-source");

        Assert.True(await db.Set<VideoTag>().AnyAsync(link => link.VideoId == video.Id && link.TagId == target.Id));
        Assert.True(await db.Set<TagParent>().AnyAsync(link => link.ParentId == target.Id && link.ChildId == child.Id));
        Assert.False(await db.Set<TagParent>().AnyAsync(link => link.ParentId == link.ChildId));
        Assert.Equal(target.Id, (await db.Segments.SingleAsync()).TagId);

        var rating = await db.Ratings.SingleAsync(row => row.UserId == user.Id && row.HostType == RatingHostType.Tag);
        Assert.Equal(target.Id, rating.HostId);
        Assert.Equal(70, rating.Value);

        var payload = (await db.Segments.SingleAsync()).Payload!.RootElement;
        Assert.Equal([target.Id, child.Id], payload.GetProperty("secondaryTagIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());

        using var objectFilter = JsonDocument.Parse((await db.SavedFilters.SingleAsync()).ObjectFilter!);
        Assert.Equal([target.Id, child.Id], objectFilter.RootElement.GetProperty("tagIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id], objectFilter.RootElement.GetProperty("tagsCriterion").GetProperty("value").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id, child.Id], objectFilter.RootElement.GetProperty("tagsCriterion").GetProperty("excludes").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id], objectFilter.RootElement.GetProperty("videoTagsCriterion").GetProperty("value").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id, child.Id], objectFilter.RootElement.GetProperty("videoTagsCriterion").GetProperty("requiredIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id], objectFilter.RootElement.GetProperty("rawTagsCriterion").GetProperty("excludes").EnumerateArray().Select(value => value.GetInt32()).ToArray());

        using var preferences = JsonDocument.Parse((await db.Users.SingleAsync()).UiPreferencesJson!);
        Assert.Equal("dark", preferences.RootElement.GetProperty("theme").GetProperty("activeThemeId").GetString());
        var defaultFilters = preferences.RootElement.GetProperty("defaultFilters");
        Assert.Equal("{", defaultFilters.GetProperty("malformed").GetString());
        using var rewrittenSegmentDefault = JsonDocument.Parse(defaultFilters.GetProperty("segments").GetString()!);
        var segmentObjectFilter = rewrittenSegmentDefault.RootElement.GetProperty("objectFilter");
        Assert.Equal([target.Id], segmentObjectFilter.GetProperty("videoTagsCriterion").GetProperty("value").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal([target.Id, child.Id], segmentObjectFilter.GetProperty("rawTagsCriterion").GetProperty("requiredIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        using var rewrittenTagDefault = JsonDocument.Parse(defaultFilters.GetProperty("tags").GetString()!);
        Assert.Equal([target.Id], rewrittenTagDefault.RootElement.GetProperty("objectFilter").GetProperty("parentsCriterion").GetProperty("value").EnumerateArray().Select(value => value.GetInt32()).ToArray());
    }

    [Fact]
    public async Task MergeAsync_AppliesDocumentedPoliciesToGenericAndSecurityReferences()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var user = new User { Username = "merge-fixture", PasswordHash = "fixture" };
        var role = new Role { Name = "Merge fixture role" };
        var group = new Group { Name = "Merge fixture group" };
        var tagReference = new CustomFieldDefinition
        {
            Key = "merge-tag-reference",
            Label = "Merge tag reference",
            Type = CustomFieldTypes.Tag,
        };
        db.AddRange(target, source, user, role, group, tagReference);
        await db.SaveChangesAsync();

        var earlier = DateTime.UtcNow.AddDays(-2);
        var later = DateTime.UtcNow.AddDays(-1);
        db.Set<TagRemoteId>().AddRange(
            new TagRemoteId { TagId = target.Id, Endpoint = "fixture", RemoteId = "CaseSensitive" },
            new TagRemoteId { TagId = source.Id, Endpoint = "fixture", RemoteId = "casesensitive" });
        db.UserBookmarks.AddRange(
            new UserBookmark { UserId = user.Id, HostType = AffinityHostType.Tag, HostId = target.Id, CreatedAt = later },
            new UserBookmark { UserId = user.Id, HostType = AffinityHostType.Tag, HostId = source.Id, CreatedAt = earlier });
        db.UserEntityAffinities.AddRange(
            new UserEntityAffinity
            {
                UserId = user.Id,
                HostType = AffinityHostType.Tag,
                HostId = target.Id,
                ViewCount = 2,
                LastConsumedAt = earlier,
                LastPositionSec = 10,
            },
            new UserEntityAffinity
            {
                UserId = user.Id,
                HostType = AffinityHostType.Tag,
                HostId = source.Id,
                IsFavorite = true,
                ViewCount = 3,
                LastConsumedAt = later,
                LastPositionSec = 20,
            });
        db.RoleEntityOverrides.AddRange(
            new RoleEntityOverride { RoleId = role.Id, EntityKind = EntityKinds.Tag, EntityId = target.Id.ToString(), Effect = "allow", AppliesTo = "read" },
            new RoleEntityOverride { RoleId = role.Id, EntityKind = EntityKinds.Tag, EntityId = source.Id.ToString(), Effect = "deny", AppliesTo = "read" });
        db.RoleContentRules.Add(new RoleContentRule
        {
            RoleId = role.Id,
            EntityKind = EntityKinds.Video,
            ScopeKind = "tag",
            ScopeValue = JsonSerializer.Serialize(new { tagId = source.Id }),
        });
        db.RoleContentRules.Add(new RoleContentRule
        {
            RoleId = role.Id,
            EntityKind = EntityKinds.Tag,
            ScopeKind = "attribute",
            ScopeValue = JsonSerializer.Serialize(new { path = "Id", @in = new[] { target.Id, source.Id } }),
        });
        db.ShareLinks.Add(new ShareLink
        {
            TokenHash = "merge-fixture-token",
            EntityKind = EntityKinds.Tag,
            EntityIds = JsonSerializer.Serialize(new[] { target.Id, source.Id }),
        });
        db.CustomFieldValues.Add(new CustomFieldValue
        {
            DefinitionId = tagReference.Id,
            EntityType = CustomFieldEntityTypes.Video,
            EntityId = 1,
            IntegerValue = source.Id,
        });
        db.FieldProvenance.Add(new FieldProvenance
        {
            HostType = AffinityHostType.Tag,
            HostId = source.Id,
            FieldKey = "description",
            SourceKey = "fixture",
        });
        db.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Tag,
            HostId = source.Id,
            TagId = source.Id,
            SourceKey = "fixture",
        });
        db.Interactions.Add(new Interaction
        {
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = source.Id,
            Kind = InteractionKind.OpenDetail,
        });
        db.GroupItems.Add(new GroupItem
        {
            GroupId = group.Id,
            Kind = GroupItemKind.Tag,
            HostType = EntityKinds.Tag,
            HostId = source.Id,
            SourceQueryJson = JsonSerializer.Serialize(new
            {
                tagIds = new[] { source.Id },
                objectFilter = new
                {
                    videoTagsCriterion = new { value = new[] { source.Id } },
                    rawTagsCriterion = new { requiredIds = new[] { source.Id } },
                },
            }),
        });
        db.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            LastHostType = InteractionHostType.Tag,
            LastHostId = source.Id,
        });
        const string opaqueQuery = "{ \"opaqueNumericId\": 2, \"note\": \"preserve formatting\" }";
        db.Groups.Add(new Group
        {
            Name = "Opaque query fixture",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = "extension-fixture",
            QueryJson = opaqueQuery,
        });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        var remoteIds = await db.Set<TagRemoteId>().Where(row => row.TagId == target.Id).OrderBy(row => row.RemoteId).ToListAsync();
        Assert.Equal(2, remoteIds.Count);
        Assert.Equal(earlier, (await db.UserBookmarks.SingleAsync()).CreatedAt);
        var affinity = await db.UserEntityAffinities.SingleAsync();
        Assert.Equal(5, affinity.ViewCount);
        Assert.True(affinity.IsFavorite);
        Assert.Equal(20, affinity.LastPositionSec);
        var roleOverride = await db.RoleEntityOverrides.SingleAsync();
        Assert.Equal(target.Id.ToString(), roleOverride.EntityId);
        Assert.Equal("deny", roleOverride.Effect);
        Assert.Equal(target.Id, (await db.CustomFieldValues.SingleAsync()).IntegerValue);
        Assert.Equal(target.Id, (await db.FieldProvenance.SingleAsync()).HostId);
        var application = await db.TagApplications.SingleAsync();
        Assert.Equal(target.Id, application.HostId);
        Assert.Equal(target.Id, application.TagId);
        Assert.Equal(target.Id, (await db.Interactions.SingleAsync()).HostId);
        var groupItem = await db.GroupItems.SingleAsync();
        Assert.Equal(target.Id, groupItem.HostId);
        using (var groupItemQuery = JsonDocument.Parse(groupItem.SourceQueryJson!))
        {
            Assert.Equal(target.Id, groupItemQuery.RootElement.GetProperty("tagIds")[0].GetInt32());
            var groupItemFilter = groupItemQuery.RootElement.GetProperty("objectFilter");
            Assert.Equal(target.Id, groupItemFilter.GetProperty("videoTagsCriterion").GetProperty("value")[0].GetInt32());
            Assert.Equal(target.Id, groupItemFilter.GetProperty("rawTagsCriterion").GetProperty("requiredIds")[0].GetInt32());
        }
        Assert.Equal(target.Id, (await db.UserSessions.SingleAsync()).LastHostId);
        var contentRules = await db.RoleContentRules.OrderBy(rule => rule.Id).ToListAsync();
        using (var scope = JsonDocument.Parse(contentRules[0].ScopeValue))
            Assert.Equal(target.Id, scope.RootElement.GetProperty("tagId").GetInt32());
        using (var scope = JsonDocument.Parse(contentRules[1].ScopeValue))
            Assert.Equal([target.Id], scope.RootElement.GetProperty("in").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        using (var entityIds = JsonDocument.Parse((await db.ShareLinks.SingleAsync()).EntityIds))
            Assert.Equal([target.Id], entityIds.RootElement.EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(opaqueQuery, (await db.Groups.SingleAsync(group => group.Name == "Opaque query fixture")).QueryJson);
    }

    [Fact]
    public async Task MergeAsync_PreservesTagApplicationMetricsWhenMappedRowsCollide()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var video = new Video { Title = "Tag application merge fixture" };
        db.AddRange(target, source, video);
        await db.SaveChangesAsync();

        db.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = video.Id,
                TagId = target.Id,
                SourceKey = "fixture",
                Confidence = 0.4f,
                HostDurationSec = 90,
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = video.Id,
                TagId = source.Id,
                SourceKey = "fixture",
                Confidence = 0.8f,
                TotalDurationSec = 20,
                HostDurationSec = 100,
            });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        var application = await db.TagApplications.SingleAsync();
        Assert.Equal(target.Id, application.TagId);
        Assert.Equal(0.8f, application.Confidence);
        Assert.Equal(20, application.TotalDurationSec);
        Assert.Equal(100, application.HostDurationSec);
    }

    [Fact]
    public async Task MergeAsync_PreservesTheProvenanceValueThatSuppliedFilledMetadata()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target", Description = " " };
        var emptySource = new Tag { Name = "Empty source" };
        var valueSource = new Tag { Name = "Value source", Description = "Transferred description" };
        db.AddRange(target, emptySource, valueSource);
        await db.SaveChangesAsync();

        db.FieldProvenance.AddRange(
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = target.Id,
                FieldKey = "description",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(" "),
                Confidence = 0.1f,
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = emptySource.Id,
                FieldKey = "description",
                SourceKey = "fixture",
                ValueJson = null,
                Confidence = 0.2f,
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = valueSource.Id,
                FieldKey = "description",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(valueSource.Description),
                Confidence = 0.8f,
            });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [emptySource.Id, valueSource.Id]);

        Assert.Equal("Transferred description", (await db.Tags.SingleAsync()).Description);
        var provenance = await db.FieldProvenance.SingleAsync();
        Assert.Equal(JsonSerializer.Serialize("Transferred description"), provenance.ValueJson);
        Assert.Equal(0.8f, provenance.Confidence);
    }

    [Fact]
    public async Task MergeAsync_UnionsSetValuedTagProvenanceCollisions()
    {
        await using var db = CreateContext();
        var target = new Tag
        {
            Name = "Target",
            Aliases = [new TagAlias { Alias = "Target alias" }],
            RemoteIds = [new TagRemoteId { Endpoint = "fixture", RemoteId = "target-remote" }],
        };
        var source = new Tag
        {
            Name = "Source",
            Aliases = [new TagAlias { Alias = "Source alias" }],
            RemoteIds = [new TagRemoteId { Endpoint = "fixture", RemoteId = "source-remote" }],
        };
        db.AddRange(target, source);
        await db.SaveChangesAsync();

        db.FieldProvenance.AddRange(
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = target.Id,
                FieldKey = "aliases",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(new[] { "Target alias", "Shared alias" }),
                Confidence = 0.5f,
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = source.Id,
                FieldKey = "aliases",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(new[] { "Source alias", "Shared alias" }),
                Confidence = 0.5f,
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = target.Id,
                FieldKey = "remote_ids",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(new[] { new { endpoint = "fixture", remoteId = "target-remote" } }),
                Confidence = 0.5f,
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Tag,
                HostId = source.Id,
                FieldKey = "remote_ids",
                SourceKey = "fixture",
                ValueJson = JsonSerializer.Serialize(new[] { new { endpoint = "fixture", remoteId = "source-remote" } }),
                Confidence = 0.8f,
            });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        var provenance = await db.FieldProvenance.ToDictionaryAsync(row => row.FieldKey);
        using var aliases = JsonDocument.Parse(provenance["aliases"].ValueJson!);
        Assert.Equal(
            ["Target alias", "Shared alias", "Source alias"],
            aliases.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(0.5f, provenance["aliases"].Confidence);

        using var remoteIds = JsonDocument.Parse(provenance["remote_ids"].ValueJson!);
        Assert.Equal(
            ["target-remote", "source-remote"],
            remoteIds.RootElement.EnumerateArray().Select(value => value.GetProperty("remoteId").GetString()!).ToArray());
        Assert.Null(provenance["remote_ids"].Confidence);
    }

    [Fact]
    public async Task MergeAsync_RewritesSegmentFieldProvenanceTagReferences()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var video = new Video { Title = "Segment provenance merge fixture" };
        db.AddRange(target, source, video);
        await db.SaveChangesAsync();
        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            TagId = source.Id,
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        db.FieldProvenance.AddRange(
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = segment.Id,
                FieldKey = "tag_id",
                ValueJson = source.Id.ToString(),
                SourceKey = "fixture",
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = segment.Id,
                FieldKey = "payload",
                ValueJson = $$"""{"secondaryTagIds":[{{source.Id}},{{target.Id}}],"unrelatedId":{{source.Id}}}""",
                SourceKey = "fixture",
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = segment.Id,
                FieldKey = "ref_id",
                ValueJson = source.Id.ToString(),
                SourceKey = "fixture",
            });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        var provenance = await db.FieldProvenance.OrderBy(row => row.FieldKey).ToDictionaryAsync(row => row.FieldKey);
        Assert.Equal(target.Id.ToString(), provenance["tag_id"].ValueJson);
        Assert.Equal(source.Id.ToString(), provenance["ref_id"].ValueJson);
        using var payload = JsonDocument.Parse(provenance["payload"].ValueJson!);
        Assert.Equal([target.Id], payload.RootElement.GetProperty("secondaryTagIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(source.Id, payload.RootElement.GetProperty("unrelatedId").GetInt32());
    }

    [Fact]
    public async Task MergeAsync_CombinesPlaybackSessionsAndRetainsTheirIntervals()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var user = new User { Username = "playback-merge-fixture", PasswordHash = "fixture" };
        db.AddRange(target, source, user);
        await db.SaveChangesAsync();
        var userSession = new UserSession { UserId = user.Id, LastHostType = InteractionHostType.Tag, LastHostId = source.Id };
        db.UserSessions.Add(userSession);
        await db.SaveChangesAsync();

        var earlier = DateTime.UtcNow.AddMinutes(-10);
        var later = DateTime.UtcNow.AddMinutes(-5);
        var targetPlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = target.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            StartedAt = earlier,
            LastSeenAt = earlier,
            LastPositionSec = 10,
            TotalWatchedSec = 5,
            Surface = "older-surface",
        };
        var sourcePlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = source.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            StartedAt = later,
            LastSeenAt = later,
            LastPositionSec = 20,
            TotalWatchedSec = 4,
            IsCompleted = true,
            Surface = "latest-surface",
            ScopeKey = "latest-scope",
            ParentHostType = InteractionHostType.Tag,
            ParentHostId = source.Id,
            Context = JsonDocument.Parse("""{"origin":"latest"}"""),
        };
        db.PlaybackSessions.AddRange(targetPlayback, sourcePlayback);
        await db.SaveChangesAsync();
        db.PlaybackIntervals.AddRange(
            new PlaybackInterval
            {
                PlaybackSessionId = targetPlayback.Id,
                UserId = user.Id,
                HostType = InteractionHostType.Tag,
                HostId = target.Id,
                StartSec = 0,
                EndSec = 5,
            },
            new PlaybackInterval
            {
                PlaybackSessionId = sourcePlayback.Id,
                UserId = user.Id,
                HostType = InteractionHostType.Tag,
                HostId = source.Id,
                StartSec = 3,
                EndSec = 7,
            });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        var mergedPlayback = await db.PlaybackSessions.SingleAsync();
        Assert.Equal(target.Id, mergedPlayback.HostId);
        Assert.Equal(earlier, mergedPlayback.StartedAt);
        Assert.Equal(later, mergedPlayback.LastSeenAt);
        Assert.Equal(20, mergedPlayback.LastPositionSec);
        Assert.Equal(7, mergedPlayback.TotalWatchedSec);
        Assert.True(mergedPlayback.IsCompleted);
        Assert.Equal("latest-surface", mergedPlayback.Surface);
        Assert.Equal("latest-scope", mergedPlayback.ScopeKey);
        Assert.Equal(target.Id, mergedPlayback.ParentHostId);
        Assert.Equal("latest", mergedPlayback.Context?.RootElement.GetProperty("origin").GetString());
        var intervals = await db.PlaybackIntervals.OrderBy(interval => interval.StartSec).ToListAsync();
        Assert.Equal(2, intervals.Count);
        Assert.All(intervals, interval => Assert.Equal(mergedPlayback.Id, interval.PlaybackSessionId));
        Assert.All(intervals, interval => Assert.Equal(target.Id, interval.HostId));
    }

    [Fact]
    public async Task MergeAsync_PreservesWatchedTimeNotRepresentedByIntervals()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var user = new User { Username = "partial-playback-merge-fixture", PasswordHash = "fixture" };
        db.AddRange(target, source, user);
        await db.SaveChangesAsync();
        var userSession = new UserSession { UserId = user.Id };
        db.UserSessions.Add(userSession);
        await db.SaveChangesAsync();

        var targetPlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = target.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            TotalWatchedSec = 5,
        };
        var sourcePlayback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = source.Id,
            SessionId = Guid.NewGuid(),
            UserSessionId = userSession.Id,
            TotalWatchedSec = 9,
        };
        db.PlaybackSessions.AddRange(targetPlayback, sourcePlayback);
        await db.SaveChangesAsync();
        db.PlaybackIntervals.Add(new PlaybackInterval
        {
            PlaybackSessionId = targetPlayback.Id,
            UserId = user.Id,
            HostType = InteractionHostType.Tag,
            HostId = target.Id,
            StartSec = 0,
            EndSec = 5,
        });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        Assert.Equal(14, (await db.PlaybackSessions.SingleAsync()).TotalWatchedSec);
    }

    [Fact]
    public async Task MergeAsync_RewritesTagPageContextInEngagementJson()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        var image = new Image { Title = "Context host fixture" };
        var user = new User { Username = "contextual-engagement-fixture", PasswordHash = "fixture" };
        db.AddRange(target, source, image, user);
        await db.SaveChangesAsync();

        var contextJson = $$"""{"tagId":{{source.Id}},"unrelatedId":{{source.Id}}}""";
        db.Interactions.Add(new Interaction
        {
            UserId = user.Id,
            HostType = InteractionHostType.Image,
            HostId = image.Id,
            Kind = InteractionKind.OpenLightbox,
            Meta = JsonDocument.Parse(contextJson),
        });
        var playback = new PlaybackSession
        {
            UserId = user.Id,
            HostType = InteractionHostType.Image,
            HostId = image.Id,
            SessionId = Guid.NewGuid(),
            Context = JsonDocument.Parse(contextJson),
        };
        db.PlaybackSessions.Add(playback);
        await db.SaveChangesAsync();
        db.PlaybackIntervals.Add(new PlaybackInterval
        {
            PlaybackSessionId = playback.Id,
            UserId = user.Id,
            HostType = InteractionHostType.Image,
            HostId = image.Id,
            StartSec = 0,
            EndSec = 1,
            Context = JsonDocument.Parse(contextJson),
        });
        await db.SaveChangesAsync();

        await new TagMergeService(db).MergeAsync(target.Id, [source.Id]);

        AssertRewritten(await db.Interactions.Select(row => row.Meta).SingleAsync());
        AssertRewritten(await db.PlaybackSessions.Select(row => row.Context).SingleAsync());
        AssertRewritten(await db.PlaybackIntervals.Select(row => row.Context).SingleAsync());

        void AssertRewritten(JsonDocument? document)
        {
            Assert.NotNull(document);
            Assert.Equal(target.Id, document.RootElement.GetProperty("tagId").GetInt32());
            Assert.Equal(source.Id, document.RootElement.GetProperty("unrelatedId").GetInt32());
        }
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class StubExternalReferenceInspector(IReadOnlyDictionary<int, int> counts)
        : ITagExternalReferenceInspector
    {
        public Task<IReadOnlyDictionary<int, int>> CountAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => Task.FromResult(counts);
    }
}
