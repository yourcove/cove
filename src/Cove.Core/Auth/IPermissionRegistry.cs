namespace Cove.Core.Auth;

/// <summary>
/// In-memory catalog of all known permissions (core + extensions). Persisted to the
/// `permissions` table on startup so the role editor can reference rich metadata.
/// </summary>
public interface IPermissionRegistry
{
    IReadOnlyList<PermissionDefinition> All { get; }
    bool IsKnown(string key);
    PermissionDefinition? Get(string key);

    /// <summary>Expand permission grants (including wildcards / implies) into the full effective set.</summary>
    HashSet<string> Expand(IEnumerable<string> grantedKeys);

    /// <summary>Register additional permissions (called by extension loader).</summary>
    IReadOnlyList<PermissionRegistrationRejection> RegisterExtensionPermissions(string extensionId, IEnumerable<PermissionDefinition> defs);
}

public sealed record PermissionRegistrationRejection(string ExtensionId, string PermissionKey, string Reason);

public static class PermissionSet
{
    /// <summary>
    /// Determines whether a permission set admits an exact permission under Cove wildcard semantics.
    /// </summary>
    public static bool Grants(IEnumerable<string> permissions, string requiredPermission)
    {
        foreach (var permission in permissions)
        {
            if (TryIntersect(permission, requiredPermission, out var intersection)
                && string.Equals(intersection, requiredPermission, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Intersects two expanded permission sets while preserving Cove wildcard semantics.
    /// </summary>
    public static HashSet<string> Intersect(
        IEnumerable<string> grantedPermissions,
        IEnumerable<string> scopedPermissions)
    {
        var granted = grantedPermissions.Distinct(StringComparer.Ordinal).ToArray();
        var scoped = scopedPermissions.Distinct(StringComparer.Ordinal).ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grant in granted)
        {
            foreach (var scope in scoped)
            {
                if (TryIntersect(grant, scope, out var permission))
                    result.Add(permission);
            }
        }

        return result;
    }

    private static bool TryIntersect(string left, string right, out string permission)
    {
        if (left == "*")
        {
            permission = right;
            return true;
        }

        if (right == "*" || string.Equals(left, right, StringComparison.Ordinal))
        {
            permission = left;
            return true;
        }

        if (TryIntersectWildcard(left, right, out permission)
            || TryIntersectWildcard(right, left, out permission))
            return true;

        permission = string.Empty;
        return false;
    }

    private static bool TryIntersectWildcard(string wildcard, string candidate, out string permission)
    {
        if (TryGetResourceWildcard(wildcard, out var resource))
        {
            if (TryGetActionWildcard(candidate, out var action))
            {
                permission = $"{resource}.{action}";
                return true;
            }

            if (TrySplitExact(candidate, out var candidateResource, out _)
                && string.Equals(resource, candidateResource, StringComparison.Ordinal))
            {
                permission = candidate;
                return true;
            }
        }

        if (TryGetActionWildcard(wildcard, out var wildcardAction)
            && TrySplitExact(candidate, out _, out var candidateAction)
            && string.Equals(wildcardAction, candidateAction, StringComparison.Ordinal))
        {
            permission = candidate;
            return true;
        }

        permission = string.Empty;
        return false;
    }

    private static bool TryGetResourceWildcard(string permission, out string resource)
    {
        if (permission.EndsWith(".*", StringComparison.Ordinal)
            && permission.Length > 2
            && permission[0] != '*'
            && !permission[..^2].Contains('.'))
        {
            resource = permission[..^2];
            return true;
        }

        resource = string.Empty;
        return false;
    }

    private static bool TryGetActionWildcard(string permission, out string action)
    {
        if (permission.StartsWith("*.", StringComparison.Ordinal)
            && permission.Length > 2
            && permission[2] != '*')
        {
            action = permission[2..];
            return true;
        }

        action = string.Empty;
        return false;
    }

    private static bool TrySplitExact(string permission, out string resource, out string action)
    {
        var separator = permission.IndexOf('.');
        if (separator > 0
            && separator < permission.Length - 1
            && permission[0] != '*'
            && permission[(separator + 1)..] != "*")
        {
            resource = permission[..separator];
            action = permission[(separator + 1)..];
            return true;
        }

        resource = string.Empty;
        action = string.Empty;
        return false;
    }
}

public sealed class PermissionRegistry : IPermissionRegistry
{
    private readonly Dictionary<string, PermissionDefinition> _byKey = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public PermissionRegistry()
    {
        foreach (var p in Permissions.CorePermissions)
            _byKey[p.Key] = p;
    }

    public IReadOnlyList<PermissionDefinition> All
    {
        get
        {
            lock (_lock) return _byKey.Values.ToList();
        }
    }

    public bool IsKnown(string key)
    {
        lock (_lock) return _byKey.ContainsKey(key);
    }

    public PermissionDefinition? Get(string key)
    {
        lock (_lock) return _byKey.TryGetValue(key, out var d) ? d : null;
    }

    public HashSet<string> Expand(IEnumerable<string> grantedKeys)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in grantedKeys)
        {
            result.Add(key);
            if (key == "*")
            {
                // superuser wildcard — leave as-is; CovePrincipal.Has shortcuts
                continue;
            }
            // expand "<resource>.*" — leave as-is so Has() can shortcut
            if (key.EndsWith(".*", StringComparison.Ordinal))
                continue;
            // expand implies recursively
            if (Get(key) is { } def && def.Implies is { Length: > 0 } implies)
            {
                foreach (var implied in implies)
                    foreach (var x in Expand([implied]))
                        result.Add(x);
            }
        }
        return result;
    }

    public IReadOnlyList<PermissionRegistrationRejection> RegisterExtensionPermissions(string extensionId, IEnumerable<PermissionDefinition> defs)
    {
        var prefix = extensionId + ".";
        var rejected = new List<PermissionRegistrationRejection>();
        lock (_lock)
        {
            foreach (var raw in defs)
            {
                var key = raw.Key;
                // Defense-in-depth: enforce extension namespace prefix; reject "*" and core-namespaced keys.
                if (key == "*")
                {
                    rejected.Add(new PermissionRegistrationRejection(extensionId, key, "Extensions cannot register the wildcard permission."));
                    continue;
                }
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    rejected.Add(new PermissionRegistrationRejection(extensionId, key, $"Permission keys must start with '{prefix}'."));
                    continue;
                }
                _byKey[key] = raw with { Source = "extension:" + extensionId };
            }
        }

        return rejected;
    }
}
