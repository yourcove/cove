using Cove.Core.DTOs;

namespace Cove.Core.Interfaces;

/// <summary>
/// Queues Cove's identify pass, for extensions, which can bind only Cove.Core.
/// </summary>
public interface IIdentifyService
{
    /// <summary>
    /// Queues an identify job and returns its job id, for polling through <see cref="IJobService.GetJob"/>.
    /// </summary>
    /// <remarks>
    /// Matches each video against the configured metadata servers and then the URL-capable scrapers,
    /// applying a match only where it clears the configured auto-apply thresholds. The scope is the
    /// videos <paramref name="options"/> names, or every video when it names none and when it is null.
    /// <para>
    /// Queued non-exclusive, so it runs beside a scan or generate rather than behind it. Returns once
    /// the job is queued, so a caller that needs the outcome polls the job rather than awaiting this.
    /// Performs no authorization, as <see cref="IScanService.StartScan"/> does not: the gate belongs
    /// on the caller's own route.
    /// </para>
    /// </remarks>
    string Start(IdentifyOptionsDto? options);
}
