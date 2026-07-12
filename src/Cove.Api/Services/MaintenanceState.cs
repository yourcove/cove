namespace Cove.Api.Services;

/// <summary>
/// Process-wide signal that the database schema is being built or rebuilt and is therefore not safe to
/// query. Two situations set it:
/// <list type="bullet">
/// <item>A database <b>restore</b>, which drops and rebuilds the whole schema.</item>
/// <item>The initial <b>baseline migration</b> on first boot against an empty database, which creates
/// every table for the first time.</item>
/// </list>
/// While either runs there are no tables to query, so request middleware consults this to short-circuit
/// DB-backed requests with a 503 instead of letting them blow up against a missing/half-built schema
/// (e.g. "relation \"users\" does not exist"). Registered as a singleton so the flag is shared across
/// all scoped request services.
/// </summary>
public sealed class MaintenanceState
{
    private volatile bool _restoreInProgress;
    private volatile bool _initializing;

    /// <summary>True while a database restore is tearing down / rebuilding the schema.</summary>
    public bool IsRestoreInProgress => _restoreInProgress;

    /// <summary>True while the initial baseline migration is creating the schema on first boot.</summary>
    public bool IsInitializing => _initializing;

    /// <summary>True whenever the schema is unavailable to query for either reason.</summary>
    public bool IsSchemaUnavailable => _restoreInProgress || _initializing;

    /// <summary>Marks a restore as running until the returned token is disposed.</summary>
    public IDisposable BeginRestore()
    {
        _restoreInProgress = true;
        return new FlagScope(() => _restoreInProgress = false);
    }

    /// <summary>Marks first-boot schema initialization as running until the returned token is disposed.</summary>
    public IDisposable BeginInitialization()
    {
        _initializing = true;
        return new FlagScope(() => _initializing = false);
    }

    private sealed class FlagScope(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            onDispose();
        }
    }
}
