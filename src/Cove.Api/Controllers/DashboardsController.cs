using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/dashboards")]
[AllowWithoutPermission]
public sealed class DashboardsController(CoveContext db, ICurrentPrincipalAccessor principals) : ControllerBase
{
    private const int MaxDashboardNameLength = 100;
    private const int MaxWidgets = 100;
    private const int MaxConfigurationBytes = 64 * 1024;
    private const int MaxLayoutBytes = 1024 * 1024;
    private const int DashboardMutationLockNamespace = 0x434F5645;
    private static readonly JsonSerializerOptions DashboardJson = CreateDashboardJsonOptions();

    [HttpPost("bootstrap")]
    public async Task<ActionResult<DashboardDto>> Bootstrap([FromBody] DashboardBootstrapRequest? request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var existing = await UserDashboards(userId).OrderByDescending(item => item.IsDefault).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return Ok(Map(existing));

        var widgets = request?.Widgets is { } supplied ? supplied : DefaultWidgets();
        if (ValidateWidgets(widgets) is { } widgetError)
            return BadRequest(new { message = widgetError });

        var dashboard = CreateEntity(userId, "Home", isDefault: true, widgets);
        db.Dashboards.Add(dashboard);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two fresh clients can bootstrap at the same time. The unique default-dashboard
            // index decides the winner; the other request returns that dashboard as well.
            db.Entry(dashboard).State = EntityState.Detached;
            existing = await UserDashboards(userId).AsNoTracking().OrderByDescending(item => item.IsDefault).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
            if (existing is null)
                throw;
            return Ok(Map(existing));
        }
        return Ok(Map(dashboard));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DashboardSummaryDto>>> List(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var dashboards = await UserDashboards(userId)
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);
        return Ok(dashboards.Select(MapSummary).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DashboardDto>> GetById(int id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var dashboard = await UserDashboards(userId).FirstOrDefaultAsync(item => item.Id == id, ct);
        return dashboard is null ? NotFound() : Ok(Map(dashboard));
    }

    [HttpPost]
    public async Task<ActionResult<DashboardDto>> Create([FromBody] DashboardCreateRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });
        if (NormalizeName(request.Name) is not { } name)
            return BadRequest(new { message = $"Dashboard name is required and cannot exceed {MaxDashboardNameLength} characters." });
        if (await NameExists(userId, name, exceptId: null, ct))
            return Conflict(new { message = "A dashboard with that name already exists." });

        var isFirst = !await UserDashboards(userId).AnyAsync(ct);
        var dashboard = CreateEntity(userId, name, isFirst, []);
        db.Dashboards.Add(dashboard);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(dashboard).State = EntityState.Detached;
            if (await NameExists(userId, name, exceptId: null, ct))
                return Conflict(new { message = "A dashboard with that name already exists." });

            // Another first-dashboard request won the default constraint with a different name.
            // Keep this create operation, but let the already-created dashboard remain default.
            dashboard = CreateEntity(userId, name, isDefault: false, []);
            db.Dashboards.Add(dashboard);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "A dashboard with that name already exists." });
            }
        }
        return CreatedAtAction(nameof(GetById), new { id = dashboard.Id }, Map(dashboard));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<DashboardDto>> Update(int id, [FromBody] DashboardUpdateRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var dashboard = await UserDashboards(userId).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (dashboard is null)
            return NotFound();
        if (dashboard.Version != request.ExpectedVersion)
            return VersionConflict(dashboard);
        if (NormalizeName(request.Name) is not { } name)
            return BadRequest(new { message = $"Dashboard name is required and cannot exceed {MaxDashboardNameLength} characters." });
        if (await NameExists(userId, name, dashboard.Id, ct))
            return Conflict(new { message = "A dashboard with that name already exists." });
        if (ValidateWidgets(request.Widgets) is { } widgetError)
            return BadRequest(new { message = widgetError });

        dashboard.Name = name;
        dashboard.NormalizedName = NormalizeNameKey(name);
        dashboard.WidgetsJson = SerializeWidgets(request.Widgets);
        dashboard.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(dashboard).State = EntityState.Detached;
            var current = await UserDashboards(userId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
            return current is null ? NotFound() : VersionConflict(current);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A dashboard with that name already exists." });
        }
        return Ok(Map(dashboard));
    }

    [HttpPost("{id:int}/duplicate")]
    public async Task<ActionResult<DashboardDto>> Duplicate(int id, [FromBody] DashboardDuplicateRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var source = await UserDashboards(userId).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
            return NotFound();
        if (NormalizeName(request.Name) is not { } name)
            return BadRequest(new { message = $"Dashboard name is required and cannot exceed {MaxDashboardNameLength} characters." });
        if (await NameExists(userId, name, exceptId: null, ct))
            return Conflict(new { message = "A dashboard with that name already exists." });

        var widgets = DeserializeWidgets(source.WidgetsJson)
            .Select(widget => widget with { InstanceId = Guid.NewGuid().ToString("N") })
            .ToList();
        var duplicate = CreateEntity(userId, name, isDefault: false, widgets);
        db.Dashboards.Add(duplicate);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A dashboard with that name already exists." });
        }
        return CreatedAtAction(nameof(GetById), new { id = duplicate.Id }, Map(duplicate));
    }

    [HttpPut("{id:int}/default")]
    public async Task<ActionResult<DashboardDto>> SetDefault(int id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        return await ExecuteUserDashboardMutationAsync<ActionResult<DashboardDto>>(userId, async operationCt =>
        {
            var dashboard = await UserDashboards(userId).FirstOrDefaultAsync(item => item.Id == id, operationCt);
            if (dashboard is null)
                return NotFound();
            if (!dashboard.IsDefault)
            {
                var currentDefaults = await UserDashboards(userId).Where(item => item.IsDefault).ToListAsync(operationCt);
                foreach (var current in currentDefaults)
                    current.IsDefault = false;
                await db.SaveChangesAsync(operationCt);

                dashboard.IsDefault = true;
                await db.SaveChangesAsync(operationCt);
            }
            return Ok(Map(dashboard));
        },
        verifySucceeded: verifyCt => UserDashboards(userId).AsNoTracking()
            .AnyAsync(item => item.Id == id && item.IsDefault, verifyCt),
        ct);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { code = "UNAUTHORIZED" });

        return await ExecuteUserDashboardMutationAsync<IActionResult>(userId, async operationCt =>
        {
            var dashboards = await UserDashboards(userId).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToListAsync(operationCt);
            var dashboard = dashboards.FirstOrDefault(item => item.Id == id);
            if (dashboard is null)
                return NotFound();
            if (dashboards.Count == 1)
                return Conflict(new { message = "The last dashboard cannot be deleted." });

            if (dashboard.IsDefault)
            {
                db.Dashboards.Remove(dashboard);
                await db.SaveChangesAsync(operationCt);

                dashboards.First(item => item.Id != id).IsDefault = true;
                await db.SaveChangesAsync(operationCt);
            }
            else
            {
                db.Dashboards.Remove(dashboard);
                await db.SaveChangesAsync(operationCt);
            }
            return NoContent();
        },
        verifySucceeded: async verifyCt => !await UserDashboards(userId).AsNoTracking()
            .AnyAsync(item => item.Id == id, verifyCt),
        ct);
    }

    private async Task<TResult> ExecuteUserDashboardMutationAsync<TResult>(
        int userId,
        Func<CancellationToken, Task<TResult>> mutation,
        Func<CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await mutation(ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteInTransactionAsync(
            operation: async operationCt =>
            {
                db.ChangeTracker.Clear();
                await AcquireUserDashboardMutationLockAsync(userId, operationCt);
                return await mutation(operationCt);
            },
            verifySucceeded: async verifyCt =>
            {
                db.ChangeTracker.Clear();
                return await verifySucceeded(verifyCt);
            },
            cancellationToken: ct);
    }

    private Task AcquireUserDashboardMutationLockAsync(int userId, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({DashboardMutationLockNamespace}, {userId})", ct);

    private IQueryable<Dashboard> UserDashboards(int userId)
        => db.Dashboards.Where(item => item.UserId == userId);

    private bool TryGetUserId(out int userId)
    {
        userId = principals.Current?.UserId ?? 0;
        return userId > 0;
    }

    private async Task<bool> NameExists(int userId, string name, int? exceptId, CancellationToken ct)
    {
        var normalized = NormalizeNameKey(name);
        return await UserDashboards(userId).AnyAsync(
            item => item.NormalizedName == normalized && (!exceptId.HasValue || item.Id != exceptId.Value), ct);
    }

    private static Dashboard CreateEntity(int userId, string name, bool isDefault, IReadOnlyList<DashboardWidgetDto> widgets)
        => new()
        {
            UserId = userId,
            Name = name,
            NormalizedName = NormalizeNameKey(name),
            IsDefault = isDefault,
            Version = 1,
            WidgetsJson = SerializeWidgets(widgets),
        };

    private static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrWhiteSpace(name) || name.Length > MaxDashboardNameLength ? null : name;
    }

    private static string NormalizeNameKey(string value) => value.Trim().ToUpperInvariant();

    private static string? ValidateWidgets(IReadOnlyList<DashboardWidgetDto>? widgets)
    {
        if (widgets is null)
            return "Dashboard widgets are required.";
        if (widgets.Count > MaxWidgets)
            return $"A dashboard cannot contain more than {MaxWidgets} widgets.";

        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        var canvasWidgets = 0;
        foreach (var widget in widgets)
        {
            if (string.IsNullOrWhiteSpace(widget.InstanceId) || widget.InstanceId.Length > 100 || !instanceIds.Add(widget.InstanceId))
                return "Every widget must have a unique instance id of at most 100 characters.";
            if (string.IsNullOrWhiteSpace(widget.Owner) || widget.Owner.Length > 200)
                return "Every widget must have an owner of at most 200 characters.";
            if (string.IsNullOrWhiteSpace(widget.WidgetKey) || widget.WidgetKey.Length > 100)
                return "Every widget must have a key of at most 100 characters.";
            if (string.IsNullOrWhiteSpace(widget.Label) || widget.Label.Length > 200)
                return "Every widget must have a label of at most 200 characters.";
            if (widget.Configuration.ValueKind is JsonValueKind.Undefined)
                return "Every widget must include JSON configuration.";
            if (!Enum.IsDefined(widget.Presentation))
                return "Every widget must use a supported dashboard presentation.";
            if (widget.Presentation == DashboardWidgetPresentation.Canvas)
                canvasWidgets++;

            var configurationBytes = JsonSerializer.SerializeToUtf8Bytes(widget.Configuration, CoveJson.Default).Length;
            if (configurationBytes > MaxConfigurationBytes)
                return $"Widget configuration cannot exceed {MaxConfigurationBytes} bytes.";
            totalBytes += configurationBytes;
        }

        if (canvasWidgets > 0 && widgets.Count != 1)
            return "A Canvas widget must be the dashboard's only widget.";

        return totalBytes > MaxLayoutBytes ? $"Dashboard configuration cannot exceed {MaxLayoutBytes} bytes." : null;
    }

    private static JsonDocument SerializeWidgets(IReadOnlyList<DashboardWidgetDto> widgets)
        => JsonSerializer.SerializeToDocument(widgets, DashboardJson);

    private static IReadOnlyList<DashboardWidgetDto> DeserializeWidgets(JsonDocument document)
        => JsonSerializer.Deserialize<List<DashboardWidgetDto>>(document.RootElement.GetRawText(), DashboardJson) ?? [];

    private static JsonSerializerOptions CreateDashboardJsonOptions()
    {
        var options = new JsonSerializerOptions(CoveJson.Default);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static DashboardDto Map(Dashboard dashboard)
        => new(dashboard.Id, dashboard.Name, dashboard.IsDefault, dashboard.Version, dashboard.CreatedAt, dashboard.UpdatedAt, DeserializeWidgets(dashboard.WidgetsJson));

    private static DashboardSummaryDto MapSummary(Dashboard dashboard)
        => new(dashboard.Id, dashboard.Name, dashboard.IsDefault, dashboard.Version, dashboard.CreatedAt, dashboard.UpdatedAt);

    private static ConflictObjectResult VersionConflict(Dashboard dashboard)
        => new(new DashboardVersionConflictDto("DASHBOARD_VERSION_CONFLICT", Map(dashboard)));

    private static IReadOnlyList<DashboardWidgetDto> DefaultWidgets() =>
    [
        CoreWidget("continue-watching", "Continue Watching", new { }),
        CoreWidget("collection", "Recently Released Videos", new { source = "premade", mode = "videos", sortBy = "date", direction = "desc", header = "Recently Released Videos" }),
        CoreWidget("collection", "Recently Added Studios", new { source = "premade", mode = "studios", sortBy = "created_at", direction = "desc", header = "Recently Added Studios" }),
        CoreWidget("collection", "Recently Released Groups", new { source = "premade", mode = "groups", sortBy = "date", direction = "desc", header = "Recently Released Groups" }),
        CoreWidget("collection", "Recently Added Performers", new { source = "premade", mode = "performers", sortBy = "created_at", direction = "desc", header = "Recently Added Performers" }),
        CoreWidget("collection", "Recently Released Galleries", new { source = "premade", mode = "galleries", sortBy = "date", direction = "desc", header = "Recently Released Galleries" }),
    ];

    private static DashboardWidgetDto CoreWidget(string key, string label, object configuration)
        => new(Guid.NewGuid().ToString("N"), "cove.core", key, label, JsonSerializer.SerializeToElement(configuration, CoveJson.Default));
}

public sealed record DashboardWidgetDto(
    string InstanceId,
    string Owner,
    string WidgetKey,
    string Label,
    JsonElement Configuration,
    DashboardWidgetPresentation Presentation = DashboardWidgetPresentation.Flow);
public sealed record DashboardDto(int Id, string Name, bool IsDefault, int Version, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<DashboardWidgetDto> Widgets);
public sealed record DashboardSummaryDto(int Id, string Name, bool IsDefault, int Version, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record DashboardBootstrapRequest(IReadOnlyList<DashboardWidgetDto>? Widgets);
public sealed record DashboardCreateRequest(string? Name);
public sealed record DashboardUpdateRequest(string? Name, int ExpectedVersion, IReadOnlyList<DashboardWidgetDto> Widgets);
public sealed record DashboardDuplicateRequest(string? Name);
public sealed record DashboardVersionConflictDto(string Code, DashboardDto Current);
