using Cove.Core.Entities.Auth;

namespace Cove.Core.Entities;

public class SavedFilter : BaseEntity
{
    public string Mode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? FindFilter { get; set; } // JSON
    public string? ObjectFilter { get; set; } // JSON
    public string? UIOptions { get; set; } // JSON

    // Owning user. Saved filters are per-user; null means an unowned/legacy row (visible when there is
    // no signed-in user, e.g. auth disabled with no owner). Owned filters are deleted with their user.
    public int? UserId { get; set; }
    public User? User { get; set; }
}
