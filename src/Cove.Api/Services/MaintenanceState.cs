namespace Cove.Api.Services;

/// <summary>
/// Process-wide signal that a destructive maintenance operation is running. Currently this is a
/// database restore, which drops and rebuilds the whole schema: while it runs there are no tables to
/// query, so request middleware consults this to short-circuit DB-backed requests with a 503 instead
/// of letting them blow up against a half-rebuilt schema (e.g. "relation \"refresh_tokens\" does not
/// exist"). Registered as a singleton so the flag is shared across all scoped request services.
/// </summary>
public sealed class MaintenanceState
{
    private volatile bool _restoreInProgress;

    /// <summary>True while a database restore is tearing down / rebuilding the schema.</summary>
    public bool IsRestoreInProgress => _restoreInProgress;

    /// <summary>Marks a restore as running until the returned token is disposed.</summary>
    public IDisposable BeginRestore()
    {
        _restoreInProgress = true;
        return new RestoreScope(this);
    }

    private sealed class RestoreScope(MaintenanceState owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._restoreInProgress = false;
        }
    }
}
