using System.Text.Json;

namespace Cove.Core.Entities.Auth;

/// <summary>A personal, ordered dashboard owned by one Cove user.</summary>
public sealed class Dashboard : BaseEntity
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public int Version { get; set; } = 1;
    public JsonDocument WidgetsJson { get; set; } = JsonDocument.Parse("[]");
}
