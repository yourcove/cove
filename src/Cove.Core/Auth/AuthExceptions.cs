namespace Cove.Core.Auth;

public class ForbiddenException : Exception
{
    public string? MissingPermission { get; }
    public EntityRef? Entity { get; }

    public ForbiddenException(string message, string? missingPermission = null, EntityRef? entity = null)
        : base(message)
    {
        MissingPermission = missingPermission;
        Entity = entity;
    }
}

public class UnauthorizedException : Exception
{
    public UnauthorizedException(string message = "Authentication required.") : base(message) { }
}

public class RefreshTokenConflictException : Exception
{
    public RefreshTokenConflictException(string message = "Refresh token was already rotated by another request.") : base(message) { }
}

/// <summary>(EntityKind, EntityId) reference used by content-rule and override checks.</summary>
public readonly record struct EntityRef(string Kind, string Id)
{
    public static EntityRef Of(string kind, int id) => new(kind, id.ToString());
    public static EntityRef Of(string kind, string id) => new(kind, id);
}
