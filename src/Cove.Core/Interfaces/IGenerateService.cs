using Cove.Core.DTOs;

namespace Cove.Core.Interfaces;

/// <summary>
/// Queues Cove's asset and fingerprint generation, for extensions, which can bind only Cove.Core.
/// </summary>
public interface IGenerateService
{
    /// <summary>
    /// Queues a generate job and returns its job id, for polling through <see cref="IJobService.GetJob"/>.
    /// </summary>
    /// <remarks>
    /// The scope is whatever <paramref name="options"/> names: the whole library when it names no ids
    /// and no paths, which is the ordinary call. Returns once the job is queued, exclusive, so the work
    /// outlives the caller and a caller that needs the outcome polls the job rather than awaiting this.
    /// <para>
    /// Performs no authorization, as <see cref="IScanService.StartScan"/> does not: the gate belongs on
    /// the caller's own route. Naming <c>VideoIds</c> without <c>Paths</c> silently skips every image,
    /// gallery, audio and text option; the HTTP route refuses that combination, this call does not.
    /// </para>
    /// </remarks>
    string Start(GenerateOptionsDto options);
}
