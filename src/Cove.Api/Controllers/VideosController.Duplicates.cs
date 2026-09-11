using System.Data;
using System.Globalization;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

public partial class VideosController
{
    private const int MaximumDuplicateGroupsPerPage = 50;

    [HttpGet("duplicates")]
    public IActionResult FindDuplicates(
        [FromQuery] string? matchType = "fingerprint",
        [FromQuery] int distance = 0,
        [FromQuery] double? durationDiff = null)
        => StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Synchronous duplicate search has been replaced by a background job. Use POST /api/videos/duplicate-searches and follow the returned search and job identifiers.",
        });

    [HttpPost("duplicate-searches")]
    [RequiresPermission(Permissions.VideosRead, Permissions.JobsRun)]
    public async Task<ActionResult<DuplicateSearchStarted>> StartDuplicateSearch(
        [FromBody] DuplicateSearchStartRequest request,
        CancellationToken ct)
    {
        var scoped = DuplicateSearchJobService.NormalizeScopePaths(request.IncludePaths).Length > 0
            || DuplicateSearchJobService.NormalizeScopePaths(request.ExcludePaths).Length > 0;
        // Folder scopes and folder keeper rules reveal which paths hold matching videos, so they need the
        // same access as the paths themselves.
        var pathRule = DuplicateKeeperRules.Normalize(request.KeeperRules).Any(rule => rule.Type == "path");
        if ((scoped || pathRule) && !CanReadFiles)
            return Forbid();
        var started = await duplicateSearchJobService!.StartAsync(
            JobOwner.FromPrincipal(principalAccessor?.Current),
            principalAccessor?.Current,
            request,
            null,
            ct);
        return Accepted(started);
    }

    [HttpGet("duplicate-searches")]
    public async Task<ActionResult<IReadOnlyList<DuplicateSearchListItem>>> ListDuplicateSearches(
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var owner = JobOwner.FromPrincipal(principalAccessor?.Current);
        var canReadAll = await Cove.Api.Hubs.JobHub.CanReadGlobalStreamAsync(principalAccessor?.Current, Permissions.JobsRead, db, ct);
        var now = DateTime.UtcNow;
        var query = db.DuplicateSearches.AsNoTracking().Where(search => search.ExpiresAt >= now);
        if (!canReadAll)
        {
            var ownerKey = owner?.Key;
            query = query.Where(search => search.OwnerKey != null && search.OwnerKey == ownerKey);
        }
        var searches = await query
            .OrderByDescending(search => search.CreatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(search => new
            {
                search.Id,
                search.MatchType,
                search.Distance,
                search.Status,
                search.GroupCount,
                search.VideoCount,
                search.IncludePaths,
                search.ExcludePaths,
                search.MinimumDuration,
                search.CreatedAt,
                search.CompletedAt,
                Unresolved = search.Groups.Count(group =>
                    (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed)
                    && group.Items.Count() >= 2),
            })
            .ToListAsync(ct);
        return Ok(searches.Select(search => new DuplicateSearchListItem(
            search.Id,
            search.MatchType,
            search.Distance,
            search.Status.ToString().ToLowerInvariant(),
            search.GroupCount,
            search.VideoCount,
            search.Unresolved,
            search.IncludePaths.Length > 0 || search.ExcludePaths.Length > 0 || search.MinimumDuration > 0,
            search.CreatedAt,
            search.CompletedAt)).ToList());
    }

    [HttpGet("duplicate-searches/{searchId:guid}")]
    public async Task<ActionResult<DuplicateSearchSummary>> GetDuplicateSearch(Guid searchId, CancellationToken ct)
    {
        var search = await GetAccessibleDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();

        var statusRows = await db.DuplicateSearchGroups
            .AsNoTracking()
            .Where(group => group.SearchId == searchId)
            .Select(group => new { group.Status, Settled = group.Items.Count() < 2 })
            .GroupBy(row => new { row.Status, row.Settled })
            .Select(rows => new { rows.Key.Status, rows.Key.Settled, Count = rows.Count() })
            .ToListAsync(ct);
        int CountOf(Func<DuplicateGroupStatus, bool, bool> predicate)
            => statusRows.Where(row => predicate(row.Status, row.Settled)).Sum(row => row.Count);
        var counts = new DuplicateGroupCounts(
            Unresolved: CountOf((status, settled) => status == DuplicateGroupStatus.Unresolved && !settled),
            Queued: CountOf((status, _) => status is DuplicateGroupStatus.Queued or DuplicateGroupStatus.Processing),
            Resolved: CountOf((status, settled) => status == DuplicateGroupStatus.Resolved
                || (status is DuplicateGroupStatus.Unresolved or DuplicateGroupStatus.Failed && settled)),
            Ignored: CountOf((status, _) => status == DuplicateGroupStatus.Ignored),
            Failed: CountOf((status, settled) => status == DuplicateGroupStatus.Failed && !settled));

        var removable = DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, searchId, ReviewableDuplicateGroups(searchId));
        var removableCount = await removable.CountAsync(ct);
        var reclaimableBytes = await db.VideoFiles
            .Where(file => file.VideoId.HasValue && removable.Contains(file.VideoId.Value))
            .SumAsync(file => (long?)file.Size, ct) ?? 0;
        var removed = await db.DuplicateSearchGroups
            .AsNoTracking()
            .Where(group => group.SearchId == searchId && group.Status == DuplicateGroupStatus.Resolved)
            .GroupBy(_ => 1)
            .Select(rows => new { Videos = rows.Sum(group => group.RemovedVideoCount), Bytes = rows.Sum(group => group.RemovedBytes) })
            .FirstOrDefaultAsync(ct);

        return Ok(new DuplicateSearchSummary(
            search.Id,
            search.JobId,
            search.MatchType,
            search.Distance,
            search.DurationDifference,
            CanReadFiles ? search.IncludePaths : [],
            CanReadFiles ? search.ExcludePaths : [],
            search.MinimumDuration,
            DuplicateKeeperRules.Deserialize(search.KeeperRulesJson)
                .Select(rule => rule.Type == "path" && !CanReadFiles ? rule with { Values = [] } : rule)
                .ToList(),
            search.Status.ToString().ToLowerInvariant(),
            search.Error,
            search.CandidateCount,
            search.GroupCount,
            search.VideoCount,
            counts,
            reclaimableBytes,
            removableCount,
            removed?.Bytes ?? 0,
            removed?.Videos ?? 0,
            search.DeletionJobId is { } jobId && !jobId.StartsWith(DuplicateSearchDeletionClaim.Prefix, StringComparison.Ordinal) ? jobId : null,
            search.CreatedAt,
            search.StartedAt,
            search.CompletedAt,
            search.ExpiresAt));
    }

    [HttpDelete("duplicate-searches/{searchId:guid}")]
    public async Task<IActionResult> DeleteDuplicateSearch(Guid searchId, CancellationToken ct)
    {
        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();
        if (search.Status is DuplicateSearchStatus.Pending or DuplicateSearchStatus.Running)
            return Conflict(new { message = "Cancel the running search from Jobs before discarding it." });
        var deleted = await db.DuplicateSearches
            .Where(item => item.Id == searchId && item.DeletionJobId == null)
            .ExecuteDeleteAsync(ct);
        return deleted == 1
            ? NoContent()
            : Conflict(new { message = "Wait for queued groups to finish resolving before discarding this search." });
    }

    [HttpGet("duplicate-searches/{searchId:guid}/groups")]
    public async Task<ActionResult<DuplicateGroupPage>> GetDuplicateSearchGroups(
        Guid searchId,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 10,
        [FromQuery] string? status = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? q = null,
        [FromQuery] string? ids = null,
        CancellationToken ct = default)
    {
        var search = await GetAccessibleDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();

        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, MaximumDuplicateGroupsPerPage);
        var groupQuery = db.DuplicateSearchGroups.AsNoTracking().Where(group => group.SearchId == searchId);
        // Explicit ids let a reviewer follow groups they just acted on after those groups leave the current filter.
        var requestedIds = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(MaximumDuplicateGroupsPerPage)
            .ToArray();
        groupQuery = requestedIds.Length > 0
            ? groupQuery.Where(group => requestedIds.Contains(group.Id))
            : FilterDuplicateGroupsByStatus(groupQuery, status);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            var canReadFiles = CanReadFiles;
            groupQuery = groupQuery.Where(group => group.Items.Any(item => item.Video != null
                && ((item.Video.Title != null && item.Video.Title.ToLower().Contains(term))
                    || item.Video.Files.Any(file => file.Basename.ToLower().Contains(term)
                        || (canReadFiles && file.Path.ToLower().Contains(term)))
                    || (item.Video.Studio != null && item.Video.Studio.Name.ToLower().Contains(term))
                    || item.Video.VideoPerformers.Any(link => link.Performer != null && link.Performer.Name.ToLower().Contains(term))
                    || item.Video.VideoTags.Any(link => link.Tag != null && link.Tag.Name.ToLower().Contains(term)))));
        }

        var totalCount = await groupQuery.CountAsync(ct);
        var ordered = (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "reclaimable" => groupQuery
                .OrderByDescending(group => group.Items.Where(item => !item.Keep).Sum(item => (long?)item.Video!.MaxFileSize) ?? 0)
                .ThenBy(group => group.Position),
            "largest" => groupQuery
                .OrderByDescending(group => group.Items.Max(item => (long?)item.Video!.MaxFileSize) ?? 0)
                .ThenBy(group => group.Position),
            "members" => groupQuery
                .OrderByDescending(group => group.Items.Count())
                .ThenBy(group => group.Position),
            "recent" => groupQuery
                .OrderByDescending(group => group.ResolvedAt ?? group.QueuedAt)
                .ThenBy(group => group.Position),
            _ => groupQuery.OrderBy(group => group.Position),
        };
        var groups = await ordered
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Include(group => group.Items)
            .ToListAsync(ct);

        var videoIds = groups.SelectMany(group => group.Items).Select(item => item.VideoId).Distinct().ToArray();
        var videos = await db.Videos
            .Include(video => video.Files).ThenInclude(file => file.Fingerprints)
            .Include(video => video.VideoTags).ThenInclude(link => link.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(video => video.VideoPerformers).ThenInclude(link => link.Performer)
            .Include(video => video.VideoGalleries).ThenInclude(link => link.Gallery)
            .Include(video => video.GroupItems).ThenInclude(item => item.Group)
            .Include(video => video.Studio)
            .Include(video => video.Urls)
            .Include(video => video.RemoteIds)
            .Where(video => videoIds.Contains(video.Id))
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, videos.Select(video => video.Id), ct);
        var videoLookup = videos.ToDictionary(
            video => video.Id,
            video => MapToDto(video, GetCustomFields(customFieldValues, video.Id)));
        var keptElsewhere = (await db.DuplicateSearchItems
                .AsNoTracking()
                .Where(item => item.Group != null && item.Group.SearchId == searchId && item.Keep && videoIds.Contains(item.VideoId))
                .Select(item => new { item.VideoId, item.GroupId })
                .ToListAsync(ct))
            .ToLookup(item => item.VideoId, item => item.GroupId);

        var items = groups.Select(group =>
        {
            var members = group.Items.Where(item => videoLookup.ContainsKey(item.VideoId)).ToArray();
            var keepIds = members.Where(item => item.Keep).Select(item => item.VideoId).ToList();
            var reclaimable = keepIds.Count == 0
                ? 0
                : members
                    .Where(item => !item.Keep && !keptElsewhere[item.VideoId].Any(groupId => groupId != group.Id))
                    .Sum(item => videoLookup[item.VideoId].Files.Sum(file => file.Size));
            var effectiveStatus = group.Status is DuplicateGroupStatus.Unresolved or DuplicateGroupStatus.Failed && members.Length < 2
                ? DuplicateGroupStatus.Resolved
                : group.Status;
            return new DuplicateGroupView(
                group.Id,
                group.Position,
                effectiveStatus.ToString().ToLowerInvariant(),
                members
                    .Select(item => videoLookup[item.VideoId])
                    .OrderBy(video => video.Id)
                    .ToList(),
                keepIds,
                group.DecisionSource,
                group.DecisionRule,
                group.ResolutionAction,
                group.DeleteFiles,
                group.Error,
                group.ResolvedAt,
                group.RemovedVideoCount,
                group.RemovedBytes,
                reclaimable);
        }).ToList();

        return Ok(new DuplicateGroupPage(items, totalCount, page, perPage));
    }

    [HttpPatch("duplicate-searches/{searchId:guid}/groups/{groupId:int}")]
    public async Task<IActionResult> UpdateDuplicateSearchGroupDecision(
        Guid searchId,
        int groupId,
        [FromBody] DuplicateKeeperDecisionRequest request,
        CancellationToken ct)
    {
        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();
        if (search.Status != DuplicateSearchStatus.Completed)
            return Conflict(new { message = "Duplicate choices can be changed after the search completes." });

        var decisionOperationId = Guid.NewGuid();
        Guid? originalDecisionOperationId = null;
        var observedOriginalDecision = false;
        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Writing the group row takes its lock until the keeper update commits. Queuing the group for
            // resolution writes the same row, so it either sees this decision or prevents it entirely.
            var decisionClaimed = await db.DuplicateSearchGroups
                .Where(item => item.SearchId == searchId
                    && item.Id == groupId
                    && (item.Status == DuplicateGroupStatus.Unresolved || item.Status == DuplicateGroupStatus.Failed))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DecisionSource, "manual"), ct);
            if (decisionClaimed == 0)
            {
                var current = await db.DuplicateSearchGroups
                    .AsNoTracking()
                    .Where(item => item.SearchId == searchId && item.Id == groupId)
                    .Select(item => new { item.LastDecisionOperationId })
                    .SingleOrDefaultAsync(ct);
                await transaction.CommitAsync(ct);
                if (current is null)
                    return (IActionResult)NotFound();
                if (current.LastDecisionOperationId == decisionOperationId)
                    return NoContent();
                return Conflict(new { message = "This group is already being resolved, so its keepers can no longer change." });
            }

            var group = await db.DuplicateSearchGroups
                .Include(item => item.Items)
                .FirstAsync(item => item.SearchId == searchId && item.Id == groupId, ct);
            if (!observedOriginalDecision)
            {
                originalDecisionOperationId = group.LastDecisionOperationId;
                observedOriginalDecision = true;
            }
            else if (group.LastDecisionOperationId == decisionOperationId)
            {
                // The previous commit succeeded and only its acknowledgement was lost.
                await transaction.CommitAsync(ct);
                return NoContent();
            }
            else if (group.LastDecisionOperationId != originalDecisionOperationId)
            {
                // A later request won while the execution strategy was deciding whether to replay.
                // Never overwrite that newer choice with this request's stale body.
                await transaction.CommitAsync(ct);
                return Conflict(new { message = "Keeper choices changed while this update was being retried. Review the duplicate group and try again." });
            }
            var keepIds = request.KeepVideoIds.Where(id => id > 0).Distinct().ToHashSet();
            if (keepIds.Count == 0)
                return BadRequest("Keep at least one video in every duplicate group.");
            var memberIds = group.Items.Select(item => item.VideoId).ToHashSet();
            if (!keepIds.IsSubsetOf(memberIds))
                return BadRequest("A keeper does not belong to this duplicate group.");
            var visibleKeeperCount = await db.Videos.CountAsync(video => keepIds.Contains(video.Id), ct);
            if (visibleKeeperCount != keepIds.Count)
                return Forbid();

            foreach (var item in group.Items)
                item.Keep = keepIds.Contains(item.VideoId);
            group.LastDecisionOperationId = decisionOperationId;
            group.DecisionSource = "manual";
            group.DecisionRule = null;
            group.Error = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return NoContent();
        });
    }

    [HttpPost("duplicate-searches/{searchId:guid}/auto-select")]
    public async Task<ActionResult<DuplicateAutoSelectResult>> AutoSelectDuplicateKeepers(
        Guid searchId,
        [FromBody] DuplicateAutoSelectRequest request,
        CancellationToken ct)
    {
        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();
        if (search.Status != DuplicateSearchStatus.Completed)
            return Conflict(new { message = "Keepers can be chosen after the search completes." });

        var rules = DuplicateKeeperRules.Normalize(request.Rules);
        if (rules.Any(rule => rule.Type == "path") && !CanReadFiles)
            return Forbid();

        var groupQuery = ReviewableDuplicateGroups(searchId);
        if (request.GroupIds is { Count: > 0 } requestedGroupIds)
            groupQuery = groupQuery.Where(group => requestedGroupIds.Contains(group.Id));
        if (!request.OverwriteManual)
            groupQuery = groupQuery.Where(group => group.DecisionSource != "manual");
        var groups = await groupQuery
            .AsNoTracking()
            .Select(group => new { group.Id, Members = group.Items.Select(item => new { item.VideoId, item.Keep }).ToList() })
            .ToListAsync(ct);

        var facts = await DuplicateKeeperRules.LoadFactsAsync(db, groups.SelectMany(group => group.Members.Select(member => member.VideoId)).ToArray(), rules, ct);
        var updated = 0;
        var changed = 0;
        foreach (var chunk in groups.Chunk(500))
        {
            var decisions = chunk.Select(group =>
            {
                var choice = DuplicateKeeperRules.Choose(group.Members.Select(member => member.VideoId).ToArray(), facts, rules);
                var alreadyChosen = group.Members.All(member => member.Keep == (member.VideoId == choice.KeeperId));
                return (group.Id, choice, alreadyChosen);
            }).ToArray();
            var ids = decisions.Select(decision => decision.Id).ToArray();

            var executionStrategy = db.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var tracked = await db.DuplicateSearchGroups
                    .Include(group => group.Items)
                    .Where(group => ids.Contains(group.Id)
                        && (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed))
                    .ToListAsync(ct);
                foreach (var group in tracked)
                {
                    var decision = decisions.First(item => item.Id == group.Id);
                    foreach (var item in group.Items)
                        item.Keep = item.VideoId == decision.choice.KeeperId;
                    group.DecisionSource = "auto";
                    group.DecisionRule = decision.choice.DecisionRule;
                    group.LastDecisionOperationId = Guid.NewGuid();
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
            updated += decisions.Length;
            changed += decisions.Count(decision => !decision.alreadyChosen);
        }

        await db.DuplicateSearches
            .Where(item => item.Id == searchId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.KeeperRulesJson, DuplicateKeeperRules.Serialize(rules)), ct);
        return Ok(new DuplicateAutoSelectResult(updated, changed));
    }

    [HttpPost("duplicate-searches/{searchId:guid}/resolve")]
    [RequiresPermission(Permissions.VideosDelete)]
    [RequiresPermissionWhenTrue(Permissions.VideosDeleteFile, ActionArgumentName = "request", PropertyName = "DeleteFiles")]
    public async Task<ActionResult<DuplicateResolveResult>> ResolveDuplicateGroups(
        Guid searchId,
        [FromBody] DuplicateResolveRequest request,
        CancellationToken ct)
    {
        var principal = principalAccessor?.Current;
        if (request.DeleteFiles && principal?.Has(Permissions.VideosDeleteFile) != true)
            return Forbid();
        var action = request.Action?.Trim().ToLowerInvariant() switch
        {
            DuplicateResolutionService.MergeAction => DuplicateResolutionService.MergeAction,
            DuplicateResolutionService.RemoveAction or null or "" => DuplicateResolutionService.RemoveAction,
            _ => null,
        };
        if (action is null)
            return BadRequest("Choose either \"remove\" or \"merge\".");
        if (action == DuplicateResolutionService.MergeAction && principal?.Has(Permissions.VideosWrite) != true)
            return Forbid();

        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();
        if (search.Status != DuplicateSearchStatus.Completed)
            return Conflict(new { message = "Wait for the duplicate search to complete before resolving groups." });

        var groupQuery = ReviewableDuplicateGroups(searchId);
        if (request.GroupIds is not null)
        {
            var requested = request.GroupIds.Distinct().ToArray();
            groupQuery = groupQuery.Where(group => requested.Contains(group.Id));
        }
        var eligibleGroupIds = await groupQuery
            .Where(group => group.Items.Any(item => item.Keep) && group.Items.Any(item => !item.Keep))
            .Select(group => group.Id)
            .ToArrayAsync(ct);
        if (eligibleGroupIds.Length == 0)
            return BadRequest("None of the selected groups have a video marked for removal.");

        // Authorize the whole destructive scope up front so a person learns about a permission problem now
        // rather than from failed groups later. The worker re-authorizes each deletion when it runs.
        var removalIds = await DuplicateSearchJobService
            .EffectiveUnkeptVideoIds(db, searchId, db.DuplicateSearchGroups.Where(group => eligibleGroupIds.Contains(group.Id)))
            .ToArrayAsync(ct);
        var deletionScopeIds = await VideoHierarchyQueries.ExpandDeletionScopeAsync(db, removalIds, ct);
        if (authorizationService is not null)
        {
            foreach (var chunk in deletionScopeIds.Chunk(4_000))
            {
                var decisions = await authorizationService.AuthorizeManyAsync(
                    principal,
                    Permissions.VideosDelete,
                    chunk.Select(id => new EntityRef(EntityKinds.Video, id.ToString(CultureInfo.InvariantCulture))).ToArray(),
                    ct);
                if (decisions.Any(decision => !decision.Allowed))
                    return Forbid();
            }
            if (action == DuplicateResolutionService.MergeAction)
            {
                var keeperIds = await db.DuplicateSearchItems
                    .Where(item => eligibleGroupIds.Contains(item.GroupId) && item.Keep)
                    .Select(item => item.VideoId)
                    .Distinct()
                    .ToArrayAsync(ct);
                foreach (var chunk in keeperIds.Chunk(4_000))
                {
                    var decisions = await authorizationService.AuthorizeManyAsync(
                        principal,
                        Permissions.VideosWrite,
                        chunk.Select(id => new EntityRef(EntityKinds.Video, id.ToString(CultureInfo.InvariantCulture))).ToArray(),
                        ct);
                    if (decisions.Any(decision => !decision.Allowed))
                        return Forbid();
                }
            }
        }

        var result = await duplicateResolutionService!.QueueAsync(
            searchId,
            eligibleGroupIds,
            action,
            request.DeleteFiles,
            request.DeleteGenerated,
            principal,
            ct);
        return Accepted(result);
    }

    [HttpPost("duplicate-searches/{searchId:guid}/groups/{groupId:int}/ignore")]
    [RequiresPermission(Permissions.VideosWrite)]
    public async Task<IActionResult> IgnoreDuplicateGroup(Guid searchId, int groupId, CancellationToken ct)
    {
        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var claimed = await db.DuplicateSearchGroups
                .Where(group => group.SearchId == searchId
                    && group.Id == groupId
                    && (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed || group.Status == DuplicateGroupStatus.Ignored))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(group => group.Status, DuplicateGroupStatus.Ignored)
                    .SetProperty(group => group.Error, (string?)null), ct);
            if (claimed == 0)
            {
                await transaction.CommitAsync(ct);
                return await db.DuplicateSearchGroups.AnyAsync(group => group.SearchId == searchId && group.Id == groupId, ct)
                    ? Conflict(new { message = "This group is already being resolved." })
                    : (IActionResult)NotFound();
            }

            var memberIds = await db.DuplicateSearchItems
                .Where(item => item.GroupId == groupId)
                .Select(item => item.VideoId)
                .OrderBy(id => id)
                .ToArrayAsync(ct);
            var pairs = memberIds
                .SelectMany((low, index) => memberIds.Skip(index + 1).Select(high => (Low: low, High: high)))
                .ToArray();
            var lows = pairs.Select(pair => pair.Low).Distinct().ToArray();
            var existing = (await db.DuplicateIgnoredPairs
                    .IgnoreQueryFilters()
                    .Where(pair => lows.Contains(pair.LowVideoId) && memberIds.Contains(pair.HighVideoId))
                    .Select(pair => new { pair.LowVideoId, pair.HighVideoId })
                    .ToListAsync(ct))
                .Select(pair => (pair.LowVideoId, pair.HighVideoId))
                .ToHashSet();
            db.DuplicateIgnoredPairs.AddRange(pairs
                .Where(pair => !existing.Contains((pair.Low, pair.High)))
                .Select(pair => new DuplicateIgnoredPair { LowVideoId = pair.Low, HighVideoId = pair.High }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return NoContent();
        });
    }

    [HttpDelete("duplicate-searches/{searchId:guid}/groups/{groupId:int}/ignore")]
    [RequiresPermission(Permissions.VideosWrite)]
    public async Task<IActionResult> RestoreIgnoredDuplicateGroup(Guid searchId, int groupId, CancellationToken ct)
    {
        var search = await GetMutableDuplicateSearchAsync(searchId, ct);
        if (search is null)
            return NotFound();

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var restored = await db.DuplicateSearchGroups
                .Where(group => group.SearchId == searchId
                    && group.Id == groupId
                    && (group.Status == DuplicateGroupStatus.Ignored || group.Status == DuplicateGroupStatus.Unresolved))
                .ExecuteUpdateAsync(update => update.SetProperty(group => group.Status, DuplicateGroupStatus.Unresolved), ct);
            if (restored == 0)
            {
                await transaction.CommitAsync(ct);
                return await db.DuplicateSearchGroups.AnyAsync(group => group.SearchId == searchId && group.Id == groupId, ct)
                    ? Conflict(new { message = "Only groups marked as not duplicates can be restored." })
                    : (IActionResult)NotFound();
            }
            var memberIds = await db.DuplicateSearchItems
                .Where(item => item.GroupId == groupId)
                .Select(item => item.VideoId)
                .ToArrayAsync(ct);
            await db.DuplicateIgnoredPairs
                .Where(pair => memberIds.Contains(pair.LowVideoId) && memberIds.Contains(pair.HighVideoId))
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return NoContent();
        });
    }

    private IQueryable<DuplicateSearchGroup> ReviewableDuplicateGroups(Guid searchId)
        => db.DuplicateSearchGroups.Where(group => group.SearchId == searchId
            && (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed)
            && group.Items.Count() >= 2);

    private static IQueryable<DuplicateSearchGroup> FilterDuplicateGroupsByStatus(IQueryable<DuplicateSearchGroup> groups, string? status)
        => (status ?? "unresolved").Trim().ToLowerInvariant() switch
        {
            "all" => groups,
            "queued" => groups.Where(group => group.Status == DuplicateGroupStatus.Queued || group.Status == DuplicateGroupStatus.Processing),
            "resolved" => groups.Where(group => group.Status == DuplicateGroupStatus.Resolved
                || ((group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed) && group.Items.Count() < 2)),
            "ignored" => groups.Where(group => group.Status == DuplicateGroupStatus.Ignored),
            "failed" => groups.Where(group => group.Status == DuplicateGroupStatus.Failed && group.Items.Count() >= 2),
            // Failed groups stay in the review list with their error so they are not lost off-screen.
            _ => groups.Where(group => (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed)
                && group.Items.Count() >= 2),
        };

    private async Task<DuplicateSearch?> GetAccessibleDuplicateSearchAsync(Guid searchId, CancellationToken ct)
    {
        var search = await db.DuplicateSearches.FirstOrDefaultAsync(item => item.Id == searchId, ct);
        if (search is null || search.ExpiresAt < DateTime.UtcNow)
            return null;
        var owner = JobOwner.FromPrincipal(principalAccessor?.Current);
        if (search.OwnerKey is not null && owner?.Key == search.OwnerKey)
            return await ReconcileDuplicateResolutionAsync(search, ct);
        return await Cove.Api.Hubs.JobHub.CanReadGlobalStreamAsync(
            principalAccessor?.Current,
            Permissions.JobsRead,
            db,
            ct)
            ? await ReconcileDuplicateResolutionAsync(search, ct)
            : null;
    }

    private async Task<DuplicateSearch?> GetMutableDuplicateSearchAsync(Guid searchId, CancellationToken ct)
    {
        var search = await db.DuplicateSearches.FirstOrDefaultAsync(item => item.Id == searchId, ct);
        if (search is null || search.ExpiresAt < DateTime.UtcNow)
            return null;

        var principal = principalAccessor?.Current;
        var owner = JobOwner.FromPrincipal(principal);
        if (search.OwnerKey is not null)
            return owner?.Key == search.OwnerKey ? await ReconcileDuplicateResolutionAsync(search, ct) : null;
        return principal?.Kind == PrincipalKind.System ? await ReconcileDuplicateResolutionAsync(search, ct) : null;
    }

    private async Task<DuplicateSearch> ReconcileDuplicateResolutionAsync(DuplicateSearch search, CancellationToken ct)
    {
        if (duplicateResolutionService is not null
            && await duplicateResolutionService.ReconcileLostWorkerAsync(search, ct))
        {
            await db.Entry(search).ReloadAsync(ct);
        }
        return search;
    }
}
