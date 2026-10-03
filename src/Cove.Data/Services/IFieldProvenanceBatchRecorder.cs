using Cove.Core.Entities;

namespace Cove.Data.Services;

/// <summary>
/// Records one field across many hosts at once, alongside <see cref="Cove.Core.Interfaces.IFieldProvenanceService"/>.
/// Face propagation touches every host a face appears on, which can be thousands, so it records their provenance
/// through this instead of one lookup per host. It lives in Cove.Data rather than Cove.Core so the extension ABI
/// does not change.
/// </summary>
public interface IFieldProvenanceBatchRecorder
{
    /// <summary>
    /// Records <paramref name="fieldKey"/> for each entry with the same semantics as calling
    /// <c>RecordAsync</c> once per entry, in order.
    /// </summary>
    Task RecordForHostsAsync(
        string fieldKey,
        IReadOnlyList<FieldProvenanceHostValue> entries,
        CancellationToken cancellationToken = default);
}

public readonly record struct FieldProvenanceHostValue(
    AffinityHostType HostType,
    int HostId,
    object? Value,
    string SourceKey,
    string? SourceRunId = null,
    float? Confidence = null);
