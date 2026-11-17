using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/segment-display-profiles")]
[RequiresPermission(Permissions.SegmentsRead)]
public class SegmentDisplayProfilesController(CoveContext db, SegmentSpanResolver spanResolver, ICurrentPrincipalAccessor? principalAccessor = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SegmentDisplayProfileDto>>> List(CancellationToken ct)
    {
        var userId = principalAccessor?.Current?.UserId;
        await spanResolver.EnsureDefaultProfileAsync(userId, ct);

        var profiles = await ApplyVisibleProfileScope(db.SegmentDisplayProfiles.AsNoTracking(), userId)
            .OrderByDescending(profile => profile.UserId == userId)
            .ThenByDescending(profile => profile.IsDefault)
            .ThenBy(profile => NaturalSort.Key(profile.Name))
            .ThenBy(profile => profile.Id)
            .ToListAsync(ct);

        return Ok(profiles.Select(MapProfile).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SegmentDisplayProfileDto>> GetById(int id, CancellationToken ct)
    {
        var userId = principalAccessor?.Current?.UserId;
        await spanResolver.EnsureDefaultProfileAsync(userId, ct);

        var profile = await ApplyVisibleProfileScope(db.SegmentDisplayProfiles.AsNoTracking(), userId)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return profile is null ? NotFound() : Ok(MapProfile(profile));
    }

    [HttpPost]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayProfileDto>> Create([FromBody] SegmentDisplayProfileCreateDto dto, CancellationToken ct)
    {
        var name = NormalizeRequiredText(dto.Name, "Profile name is required.");
        if (name is null)
            return BadRequest("Profile name is required.");

        var userId = principalAccessor?.Current?.UserId;
        var firstInScope = !await ApplyOwnerProfileScope(db.SegmentDisplayProfiles.AsNoTracking(), userId).AnyAsync(ct);
        if (dto.IsDefault)
            await ClearDefaultsAsync(userId, exceptProfileId: null, ct);

        var profile = new SegmentDisplayProfile
        {
            Name = name,
            Description = NormalizeOptionalText(dto.Description),
            UserId = userId,
            IsDefault = dto.IsDefault || firstInScope,
            Version = 1,
        };

        db.SegmentDisplayProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = profile.Id }, MapProfile(profile));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayProfileDto>> Update(int id, [FromBody] SegmentDisplayProfileUpdateDto dto, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return error!;

        var name = NormalizeRequiredText(dto.Name, "Profile name is required.");
        if (name is null)
            return BadRequest("Profile name is required.");

        profile.Name = name;
        profile.Description = NormalizeOptionalText(dto.Description);

        await db.SaveChangesAsync(ct);
        return Ok(MapProfile(profile));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.SegmentsDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return error!;
        if (profile.IsSystem)
            return Forbid();

        var scopeUserId = profile.UserId;
        var wasDefault = profile.IsDefault;
        db.SegmentDisplayProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);

        if (wasDefault)
            await AssignFallbackDefaultAsync(scopeUserId, ct);

        spanResolver.EvictProfile(id);
        return NoContent();
    }

    [HttpPut("{id:int}/default")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayProfileDto>> SetDefault(int id, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return error!;

        await ClearDefaultsAsync(profile.UserId, profile.Id, ct);
        profile.IsDefault = true;
        await db.SaveChangesAsync(ct);

        return Ok(MapProfile(profile));
    }

    [HttpGet("{profileId:int}/rules")]
    public async Task<ActionResult<IReadOnlyList<SegmentDisplayRuleDto>>> ListRules(int profileId, CancellationToken ct)
    {
        var profile = await LoadVisibleProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound();

        var rules = await db.SegmentDisplayRules.AsNoTracking()
            .Include(rule => rule.Tag)
            .Where(rule => rule.ProfileId == profileId)
            .OrderByDescending(rule => rule.Priority ?? 0)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);

        return Ok(rules.Select(MapRule).ToList());
    }

    [HttpPost("{profileId:int}/rules")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayRuleDto>> CreateRule(int profileId, [FromBody] SegmentDisplayRuleCreateDto dto, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return error!;

        var rule = new SegmentDisplayRule
        {
            ProfileId = profile.Id,
            SourceKey = dto.SourceKey,
            Kind = dto.Kind,
            TagId = dto.TagId,
            TagCategory = dto.TagCategory,
            HostType = dto.HostType,
            Visible = dto.Visible,
            MinConfidence = dto.MinConfidence,
            MinDurationSec = dto.MinDurationSec,
            MergeGapSec = dto.MergeGapSec,
            CollapseToInstant = dto.CollapseToInstant,
            ColorOverride = dto.ColorOverride,
            Lane = dto.Lane,
            Priority = dto.Priority,
            UserId = profile.UserId,
        };

        // The database moves the profile to a new version whenever its rules change (see
        // SegmentSpanIndexSqlDefinitions), here and for every writer that does not come through this
        // controller, so the rule endpoints do not bump it themselves.
        db.SegmentDisplayRules.Add(rule);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        await LoadTagAsync(rule, ct);

        return Created($"/api/segment-display-profiles/{profileId}/rules/{rule.Id}", MapRule(rule));
    }

    [HttpPost("{profileId:int}/rules/bulk")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<IActionResult> CreateRulesBulk(int profileId, [FromBody] List<SegmentDisplayRuleCreateDto>? dtos, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return error!;

        var ruleDtos = dtos ?? [];
        if (ruleDtos.Count == 0)
            return NoContent();

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var retryProfile = (await LoadEditableProfileAsync(profileId, ct)).Profile
                ?? throw new InvalidOperationException($"Segment display profile {profileId} disappeared during bulk rule creation.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var rules = ruleDtos.Select(dto => new SegmentDisplayRule
            {
                ProfileId = retryProfile.Id,
                SourceKey = dto.SourceKey,
                Kind = dto.Kind,
                TagId = dto.TagId,
                TagCategory = dto.TagCategory,
                HostType = dto.HostType,
                Visible = dto.Visible,
                MinConfidence = dto.MinConfidence,
                MinDurationSec = dto.MinDurationSec,
                MergeGapSec = dto.MergeGapSec,
                CollapseToInstant = dto.CollapseToInstant,
                ColorOverride = dto.ColorOverride,
                Lane = dto.Lane,
                Priority = dto.Priority,
                UserId = retryProfile.UserId,
            }).ToList();

            db.SegmentDisplayRules.AddRange(rules);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        spanResolver.EvictProfile(profile.Id);
        return NoContent();
    }

    [HttpPut("{profileId:int}/rules/{id:int}")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayRuleDto>> UpdateRule(int profileId, int id, [FromBody] SegmentDisplayRuleUpdateDto dto, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return error!;

        var rule = await db.SegmentDisplayRules
            .FirstOrDefaultAsync(item => item.ProfileId == profileId && item.Id == id, ct);
        if (rule is null)
            return NotFound();

        rule.SourceKey = dto.SourceKey;
        rule.Kind = dto.Kind;
        rule.TagId = dto.TagId;
        rule.TagCategory = dto.TagCategory;
        rule.HostType = dto.HostType;
        rule.Visible = dto.Visible;
        rule.MinConfidence = dto.MinConfidence;
        rule.MinDurationSec = dto.MinDurationSec;
        rule.MergeGapSec = dto.MergeGapSec;
        rule.CollapseToInstant = dto.CollapseToInstant;
        rule.ColorOverride = dto.ColorOverride;
        rule.Lane = dto.Lane;
        rule.Priority = dto.Priority;
        rule.UserId = profile.UserId;
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        await LoadTagAsync(rule, ct);

        return Ok(MapRule(rule));
    }

    [HttpDelete("{profileId:int}/rules/{id:int}")]
    [RequiresPermission(Permissions.SegmentsDelete)]
    public async Task<IActionResult> DeleteRule(int profileId, int id, CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return error!;

        var rule = await db.SegmentDisplayRules.FirstOrDefaultAsync(item => item.ProfileId == profileId && item.Id == id, ct);
        if (rule is null)
            return NotFound();

        db.SegmentDisplayRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        return NoContent();
    }

    /// <summary>
    /// Saves a profile's complete rule list in one transaction, which is what the profile editor's Save button
    /// sends. Changing a profile's rules rebuilds its stored segments in the background, so saving the edits
    /// together starts that work once instead of once per edit. Rules whose values did not change are not
    /// written, and a save that changes nothing leaves the profile's version alone.
    /// </summary>
    [HttpPut("{profileId:int}/rules")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<SegmentDisplayRulesSavedDto>> ReplaceRules(
        int profileId,
        [FromBody] SegmentDisplayRulesReplaceDto dto,
        [FromServices] SegmentSpanIndexService spanIndex,
        [FromServices] SegmentSpanIndexSignal spanIndexSignal,
        CancellationToken ct)
    {
        var (profile, error) = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return error!;

        var items = dto.Rules ?? [];
        var keptIds = items.Where(item => item.Id.HasValue).Select(item => item.Id!.Value).ToList();
        if (keptIds.Count != keptIds.Distinct().Count())
            return BadRequest("Each rule can appear only once.");

        var outcome = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Lock the profile so two saves from the same version cannot both pass the check below. NO KEY
            // UPDATE still serialises saves and the rule trigger's version bump, without waiting on the
            // key-share locks the span index's foreign keys take while it writes this profile's rows.
            var currentVersion = await db.Database
                .SqlQuery<int?>($"""SELECT "Version" AS "Value" FROM segment_display_profiles WHERE "Id" = {profileId} FOR NO KEY UPDATE""")
                .FirstOrDefaultAsync(ct);
            if (currentVersion is null)
                return ReplaceOutcome.ProfileGone;
            if (currentVersion != dto.ExpectedVersion)
                return ReplaceOutcome.Conflict;

            var existing = await db.SegmentDisplayRules.Where(rule => rule.ProfileId == profileId).ToListAsync(ct);
            var existingById = existing.ToDictionary(rule => rule.Id);
            if (keptIds.Any(id => !existingById.ContainsKey(id)))
                return ReplaceOutcome.UnknownRule;

            var removed = existing.Where(rule => !keptIds.Contains(rule.Id)).ToList();
            if (removed.Count > 0 && principalAccessor?.Current?.Has(Permissions.SegmentsDelete) != true)
                return ReplaceOutcome.Forbidden;

            db.SegmentDisplayRules.RemoveRange(removed);
            foreach (var item in items)
            {
                var rule = item.Id is { } id ? existingById[id] : db.SegmentDisplayRules.Add(new SegmentDisplayRule { ProfileId = profileId }).Entity;
                rule.SourceKey = item.SourceKey;
                rule.Kind = item.Kind;
                rule.TagId = item.TagId;
                rule.TagCategory = item.TagCategory;
                rule.HostType = item.HostType;
                rule.Visible = item.Visible;
                rule.MinConfidence = item.MinConfidence;
                rule.MinDurationSec = item.MinDurationSec;
                rule.MergeGapSec = item.MergeGapSec;
                rule.CollapseToInstant = item.CollapseToInstant;
                rule.ColorOverride = item.ColorOverride;
                rule.Lane = item.Lane;
                rule.Priority = item.Priority;
                rule.UserId = profile.UserId;
            }

            db.ChangeTracker.DetectChanges();
            var changed = db.ChangeTracker.HasChanges();
            if (changed)
                await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return changed ? ReplaceOutcome.Changed : ReplaceOutcome.Unchanged;
        });

        switch (outcome)
        {
            case ReplaceOutcome.Conflict:
                return Conflict(new { code = "PROFILE_RULES_CHANGED", message = "The profile's rules were changed elsewhere since they were loaded." });
            case ReplaceOutcome.UnknownRule:
                return BadRequest("A rule in the list does not belong to this profile.");
            case ReplaceOutcome.Forbidden:
                return Forbid();
            case ReplaceOutcome.ProfileGone:
                return NotFound();
        }

        if (outcome == ReplaceOutcome.Changed)
        {
            spanResolver.EvictProfile(profileId);
            // Start building the profile's stored segments now, rather than the next time it is opened or
            // the background pass next wakes up.
            await spanIndex.RegisterProfileAsync(profileId, ct);
            spanIndexSignal.Notify();
        }

        db.ChangeTracker.Clear();
        var saved = await db.SegmentDisplayProfiles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == profileId, ct);
        if (saved is null)
            return NotFound();
        var rules = await db.SegmentDisplayRules.AsNoTracking()
            .Include(rule => rule.Tag)
            .Where(rule => rule.ProfileId == profileId)
            .OrderByDescending(rule => rule.Priority ?? 0)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);
        return Ok(new SegmentDisplayRulesSavedDto(MapProfile(saved), rules.Select(MapRule).ToList(), outcome == ReplaceOutcome.Changed));
    }

    private enum ReplaceOutcome { Changed, Unchanged, Conflict, UnknownRule, Forbidden, ProfileGone }

    [HttpPost("preview")]
    [RequiresPermission(Permissions.SegmentsRead)]
    public async Task<ActionResult<ResolvedSpanListDto>> Preview([FromBody] SegmentDisplayProfilePreviewRequestDto dto, CancellationToken ct)
    {
        if (dto.VideoId <= 0)
            return BadRequest("VideoId is required.");

        var videoExists = await db.Videos.AsNoTracking().AnyAsync(video => video.Id == dto.VideoId, ct);
        if (!videoExists)
            return NotFound();

        var ruleInputs = dto.Rules ?? [];
        var rules = ruleInputs
            .Select((rule, index) => new SegmentDisplayRule
            {
                ProfileId = 0,
                SourceKey = NormalizeOptionalText(rule.SourceKey),
                Kind = NormalizeOptionalText(rule.Kind),
                TagId = rule.TagId,
                TagCategory = NormalizeOptionalText(rule.TagCategory),
                HostType = rule.HostType,
                Visible = rule.Visible,
                MinConfidence = rule.MinConfidence,
                MinDurationSec = rule.MinDurationSec,
                MergeGapSec = rule.MergeGapSec,
                CollapseToInstant = rule.CollapseToInstant,
                ColorOverride = NormalizeOptionalText(rule.ColorOverride),
                Lane = rule.Lane,
                Priority = rule.Priority ?? (ruleInputs.Count - index),
                UserId = principalAccessor?.Current?.UserId,
            })
            .ToList();

        var spans = await spanResolver.PreviewVideoAsync(dto.VideoId, rules, ct);
        return Ok(new ResolvedSpanListDto(spans.ToList()));
    }

    private Task<SegmentDisplayProfile?> LoadVisibleProfileAsync(int id, CancellationToken ct)
        => ApplyVisibleProfileScope(db.SegmentDisplayProfiles.AsNoTracking(), principalAccessor?.Current?.UserId)
            .FirstOrDefaultAsync(profile => profile.Id == id, ct);

    /// <summary>
    /// Loads a profile for a write. Users edit their own profiles; the shared profiles (including the built-ins,
    /// which every user falls back to) are editable only by admins, since a change there applies to everyone.
    /// A shared profile the caller can see but not edit is 403, not 404.
    /// </summary>
    private async Task<(SegmentDisplayProfile? Profile, ActionResult? Error)> LoadEditableProfileAsync(int id, CancellationToken ct)
    {
        var principal = principalAccessor?.Current;
        var profile = await ApplyVisibleProfileScope(db.SegmentDisplayProfiles, principal?.UserId)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (profile is null)
            return (null, NotFound());

        var canEdit = profile.UserId == principal?.UserId
            || (profile.UserId == null && principal?.Has(Permissions.SystemSettingsWrite) == true);
        return canEdit ? (profile, null) : (null, Forbid());
    }

    private static IQueryable<SegmentDisplayProfile> ApplyVisibleProfileScope(IQueryable<SegmentDisplayProfile> query, int? userId)
    {
        if (userId.HasValue)
            return query.Where(profile => profile.UserId == null || profile.UserId == userId.Value);

        return query.Where(profile => profile.UserId == null);
    }

    /// <summary>The profiles that share one owner's default slot: that user's own, or the shared ones for null.</summary>
    private static IQueryable<SegmentDisplayProfile> ApplyOwnerProfileScope(IQueryable<SegmentDisplayProfile> query, int? userId)
    {
        if (userId.HasValue)
            return query.Where(profile => profile.UserId == userId.Value);

        return query.Where(profile => profile.UserId == null);
    }

    private async Task ClearDefaultsAsync(int? userId, int? exceptProfileId, CancellationToken ct)
    {
        var defaults = await ApplyOwnerProfileScope(db.SegmentDisplayProfiles, userId)
            .Where(profile => profile.IsDefault && (!exceptProfileId.HasValue || profile.Id != exceptProfileId.Value))
            .ToListAsync(ct);

        foreach (var existing in defaults)
            existing.IsDefault = false;
    }

    private async Task AssignFallbackDefaultAsync(int? userId, CancellationToken ct)
    {
        var fallback = await ApplyOwnerProfileScope(db.SegmentDisplayProfiles, userId)
            .OrderBy(profile => profile.Id)
            .FirstOrDefaultAsync(ct);
        if (fallback is null)
            return;

        fallback.IsDefault = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task LoadTagAsync(SegmentDisplayRule rule, CancellationToken ct)
    {
        if (rule.TagId.HasValue)
            await db.Entry(rule).Reference(item => item.Tag).LoadAsync(ct);
    }

    private static string? NormalizeRequiredText(string? value, string _)
    {
        var normalized = NormalizeOptionalText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SegmentDisplayProfileDto MapProfile(SegmentDisplayProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.Description,
        profile.UserId,
        profile.IsSystem,
        profile.IsDefault,
        profile.Version,
        profile.CreatedAt.ToString("o"),
        profile.UpdatedAt.ToString("o"));

    private static SegmentDisplayRuleDto MapRule(SegmentDisplayRule rule) => new(
        rule.Id,
        rule.SourceKey,
        rule.Kind,
        rule.TagId,
        rule.Tag?.Name,
        rule.TagCategory,
        rule.HostType,
        rule.Visible,
        rule.MinConfidence,
        rule.MinDurationSec,
        rule.MergeGapSec,
        rule.CollapseToInstant,
        rule.ColorOverride,
        rule.Lane,
        rule.Priority,
        rule.UserId,
        rule.CreatedAt.ToString("o"),
        rule.UpdatedAt.ToString("o"));
}
