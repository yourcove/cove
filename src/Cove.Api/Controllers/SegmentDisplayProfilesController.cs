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
            .ThenBy(profile => profile.Name)
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
        var firstInScope = !await ApplyEditableProfileScope(db.SegmentDisplayProfiles.AsNoTracking(), userId).AnyAsync(ct);
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
        var profile = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return NotFound();

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
        var profile = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return NotFound();
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
        var profile = await LoadEditableProfileAsync(id, ct);
        if (profile is null)
            return NotFound();

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
        var profile = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound();

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

        db.SegmentDisplayRules.Add(rule);
        BumpProfileVersion(profile);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        await LoadTagAsync(rule, ct);

        return Created($"/api/segment-display-profiles/{profileId}/rules/{rule.Id}", MapRule(rule));
    }

    [HttpPost("{profileId:int}/rules/bulk")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<IActionResult> CreateRulesBulk(int profileId, [FromBody] List<SegmentDisplayRuleCreateDto>? dtos, CancellationToken ct)
    {
        var profile = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound();

        var ruleDtos = dtos ?? [];
        if (ruleDtos.Count == 0)
            return NoContent();

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var retryProfile = await LoadEditableProfileAsync(profileId, ct)
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
            BumpProfileVersion(retryProfile);
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
        var profile = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound();

        var rule = await db.SegmentDisplayRules
            .Include(item => item.Tag)
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
        rule.Tag = null;

        BumpProfileVersion(profile);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        await LoadTagAsync(rule, ct);

        return Ok(MapRule(rule));
    }

    [HttpDelete("{profileId:int}/rules/{id:int}")]
    [RequiresPermission(Permissions.SegmentsDelete)]
    public async Task<IActionResult> DeleteRule(int profileId, int id, CancellationToken ct)
    {
        var profile = await LoadEditableProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound();

        var rule = await db.SegmentDisplayRules.FirstOrDefaultAsync(item => item.ProfileId == profileId && item.Id == id, ct);
        if (rule is null)
            return NotFound();

        db.SegmentDisplayRules.Remove(rule);
        BumpProfileVersion(profile);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictProfile(profile.Id);
        return NoContent();
    }

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

    private Task<SegmentDisplayProfile?> LoadEditableProfileAsync(int id, CancellationToken ct)
        => ApplyEditableProfileScope(db.SegmentDisplayProfiles, principalAccessor?.Current?.UserId)
            .FirstOrDefaultAsync(profile => profile.Id == id, ct);

    private static IQueryable<SegmentDisplayProfile> ApplyVisibleProfileScope(IQueryable<SegmentDisplayProfile> query, int? userId)
    {
        if (userId.HasValue)
            return query.Where(profile => profile.UserId == null || profile.UserId == userId.Value);

        return query.Where(profile => profile.UserId == null);
    }

    private static IQueryable<SegmentDisplayProfile> ApplyEditableProfileScope(IQueryable<SegmentDisplayProfile> query, int? userId)
    {
        if (userId.HasValue)
            return query.Where(profile => profile.UserId == userId.Value);

        return query.Where(profile => profile.UserId == null);
    }

    private async Task ClearDefaultsAsync(int? userId, int? exceptProfileId, CancellationToken ct)
    {
        var defaults = await ApplyEditableProfileScope(db.SegmentDisplayProfiles, userId)
            .Where(profile => profile.IsDefault && (!exceptProfileId.HasValue || profile.Id != exceptProfileId.Value))
            .ToListAsync(ct);

        foreach (var existing in defaults)
            existing.IsDefault = false;
    }

    private async Task AssignFallbackDefaultAsync(int? userId, CancellationToken ct)
    {
        var fallback = await ApplyEditableProfileScope(db.SegmentDisplayProfiles, userId)
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

    private static void BumpProfileVersion(SegmentDisplayProfile profile)
        => profile.Version = Math.Max(profile.Version, 1) + 1;

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
