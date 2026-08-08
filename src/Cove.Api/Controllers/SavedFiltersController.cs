using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.SavedFiltersRead)]
public class SavedFiltersController(ISavedFilterRepository filterRepo, ICurrentPrincipalAccessor principals) : ControllerBase
{
    // Saved filters are per-user: every operation is scoped to the calling user's id (null when there
    // is no signed-in user, e.g. auth disabled with no owner — those callers share the unowned rows).
    private int? CurrentUserId => principals.Current?.UserId;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SavedFilterDto>>> GetAll([FromQuery] string? mode, CancellationToken ct)
    {
        IReadOnlyList<SavedFilter> filters;
        if (mode != null)
        {
            if (!TryNormalizeMode(mode, out var filterMode))
                return BadRequest(new { message = $"Invalid filter mode: {mode}" });

            filters = await filterRepo.GetByModeForUserAsync(filterMode, CurrentUserId, ct);
        }
        else
            filters = await filterRepo.GetAllForUserAsync(CurrentUserId, ct);

        return Ok(filters
            .Where(IsVisibleToCurrentUser)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(MapToDto)
            .ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SavedFilterDto>> GetById(int id, CancellationToken ct)
    {
        var filter = await filterRepo.GetByIdAsync(id, ct);
        if (filter == null || !IsVisibleToCurrentUser(filter)) return NotFound();
        return Ok(MapToDto(filter));
    }

    [HttpPost]
    [RequiresPermission(Permissions.SavedFiltersWrite)]
    public async Task<ActionResult<SavedFilterDto>> Create([FromBody] SavedFilterCreateDto dto, CancellationToken ct)
    {
        if (!TryNormalizeMode(dto.Mode, out var filterMode))
            return BadRequest(new { message = $"Invalid filter mode: {dto.Mode}" });

        var name = dto.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return BadRequest(new { message = "A saved filter name is required." });
        if (await HasDuplicateName(filterMode, name, null, ct))
            return Conflict(new { message = "A saved filter with this name already exists." });

        var filter = new SavedFilter
        {
            Name = name, Mode = filterMode, UserId = CurrentUserId,
            FindFilter = StripRandomSeed(dto.FindFilter), ObjectFilter = dto.ObjectFilter, UIOptions = dto.UIOptions
        };

        filter = await filterRepo.AddAsync(filter, ct);
        return CreatedAtAction(nameof(GetById), new { id = filter.Id }, MapToDto(filter));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.SavedFiltersWrite)]
    public async Task<ActionResult<SavedFilterDto>> Update(int id, [FromBody] SavedFilterUpdateDto dto, CancellationToken ct)
    {
        var filter = await filterRepo.GetByIdAsync(id, ct);
        if (filter == null || !IsVisibleToCurrentUser(filter)) return NotFound();

        var mode = filter.Mode;
        if (dto.Mode != null && !TryNormalizeMode(dto.Mode, out mode))
            return BadRequest(new { message = $"Invalid filter mode: {dto.Mode}" });

        var name = (dto.Name ?? filter.Name).Trim();
        if (name.Length == 0)
            return BadRequest(new { message = "A saved filter name is required." });
        if (await HasDuplicateName(mode, name, id, ct))
            return Conflict(new { message = "A saved filter with this name already exists." });

        filter.Name = name;
        filter.Mode = mode;
        if (dto.FindFilter != null) filter.FindFilter = StripRandomSeed(dto.FindFilter);
        if (dto.ObjectFilter != null) filter.ObjectFilter = dto.ObjectFilter;
        if (dto.UIOptions != null) filter.UIOptions = dto.UIOptions;

        await filterRepo.UpdateAsync(filter, ct);
        return Ok(MapToDto(filter));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.SavedFiltersDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var f = await filterRepo.GetByIdAsync(id, ct);
        if (f == null || !IsVisibleToCurrentUser(f)) return NotFound();
        await filterRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    // Defense-in-depth: the repository already scopes by user, but guard cross-user access on by-id
    // lookups too. A null-owned (legacy/unowned) row is only visible when the caller is also unowned.
    private bool IsVisibleToCurrentUser(SavedFilter filter) => filter.UserId == CurrentUserId;

    private async Task<bool> HasDuplicateName(string mode, string name, int? excludedId, CancellationToken ct)
    {
        var filters = await filterRepo.GetByModeForUserAsync(mode, CurrentUserId, ct);
        return filters.Any(filter =>
            filter.Id != excludedId
            && IsVisibleToCurrentUser(filter)
            && string.Equals(filter.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryNormalizeMode(string? value, out string mode)
    {
        mode = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim().ToLowerInvariant();
        if (Enum.TryParse<Core.Enums.FilterMode>(normalized, true, out var builtIn)
            && Enum.IsDefined(builtIn)
            && !int.TryParse(normalized, out _))
        {
            mode = builtIn.ToString().ToLowerInvariant();
            return true;
        }

        if (normalized.Length > 200 || !normalized.StartsWith("ext:", StringComparison.Ordinal)) return false;
        var parts = normalized.Split(':');
        if (parts.Length != 3 || !IsScopePart(parts[1], allowDots: true) || !IsScopePart(parts[2], allowDots: false)) return false;

        mode = normalized;
        return true;
    }

    private static bool IsScopePart(string value, bool allowDots)
    {
        if (value.Length is < 1 or > 100) return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character == '-'
            || character == '_'
            || (allowDots && character == '.'));
    }

    // When a filter using random sort is persisted, drop the random seed so the saved/default
    // filter re-shuffles on every load instead of reproducing the same "random" order forever.
    internal static string? StripRandomSeed(string? findFilterJson)
    {
        if (string.IsNullOrWhiteSpace(findFilterJson)) return findFilterJson;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(findFilterJson);
        }
        catch (JsonException)
        {
            return findFilterJson;
        }

        if (node is not JsonObject obj) return findFilterJson;

        var sort = obj.TryGetPropertyValue("sort", out var sortNode) ? sortNode?.GetValue<string>() : null;
        if (!string.Equals(sort, "random", StringComparison.OrdinalIgnoreCase)) return findFilterJson;

        if (!obj.ContainsKey("seed")) return findFilterJson;

        obj.Remove("seed");
        return obj.ToJsonString();
    }

    private static SavedFilterDto MapToDto(SavedFilter f) => new(
        f.Id, f.Mode, f.Name, f.FindFilter, f.ObjectFilter, f.UIOptions);
}

public record SavedFilterDto(int Id, string Mode, string Name, string? FindFilter, string? ObjectFilter, string? UIOptions);
public record SavedFilterCreateDto(string Mode, string Name, string? FindFilter, string? ObjectFilter, string? UIOptions);
public record SavedFilterUpdateDto(string? Mode, string? Name, string? FindFilter, string? ObjectFilter, string? UIOptions);
