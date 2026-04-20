using Cove.Plugins;

namespace Cove.Sdk;

/// <summary>
/// Base class for extensions that register background jobs.
/// Provides fluent job registration and typed dispatching.
/// </summary>
public abstract class JobExtensionBase : CoveExtensionBase, IJobExtension
{
    private readonly List<ExtensionJobDefinition> _jobs = [];
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>?, IJobProgress, CancellationToken, Task>> _runners = [];

    protected JobExtensionBase()
    {
        DefineJobs();
    }

    public IReadOnlyList<ExtensionJobDefinition> Jobs => _jobs;

    /// <summary>
    /// Override to register jobs using <see cref="Job"/>.
    /// </summary>
    protected abstract void DefineJobs();

    /// <summary>
    /// Register a background job with its handler.
    /// </summary>
    protected void Job(
        string id,
        string name,
        Func<IReadOnlyDictionary<string, string>?, IJobProgress, CancellationToken, Task> handler,
        string? description = null,
        bool supportsParameters = false)
    {
        _jobs.Add(new ExtensionJobDefinition(id, name, description, supportsParameters));
        _runners[id] = handler;
    }

    public Task RunJobAsync(string jobId, IReadOnlyDictionary<string, string>? parameters, IJobProgress progress, CancellationToken ct)
    {
        if (_runners.TryGetValue(jobId, out var runner))
            return runner(parameters, progress, ct);
        throw new InvalidOperationException($"Unknown job '{jobId}' in extension '{Id}'");
    }
}
