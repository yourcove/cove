using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class FieldProvenanceService(CoveContext db) : IFieldProvenanceService, IFieldProvenanceBatchRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(
        AffinityHostType hostType,
        int hostId,
        string fieldKey,
        object? value,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        CancellationToken cancellationToken = default)
    {
        if (hostId <= 0 || string.IsNullOrWhiteSpace(fieldKey))
            return;

        var normalizedFieldKey = fieldKey.Trim().ToLowerInvariant();
        var normalizedSourceKey = NormalizeSourceKey(sourceKey);
        var normalizedSourceRunId = NormalizeOptional(sourceRunId);
        var normalizedModelKey = NormalizeOptional(modelKey);
        var valueJson = SerializeValue(value);

        var provenance = db.FieldProvenance.Local.FirstOrDefault(candidate =>
            candidate.HostType == hostType
            && candidate.HostId == hostId
            && candidate.FieldKey == normalizedFieldKey
            && candidate.SourceKey == normalizedSourceKey
            && candidate.SourceRunId == normalizedSourceRunId
            && candidate.ModelKey == normalizedModelKey);

        provenance ??= await db.FieldProvenance.FirstOrDefaultAsync(candidate =>
            candidate.HostType == hostType
            && candidate.HostId == hostId
            && candidate.FieldKey == normalizedFieldKey
            && candidate.SourceKey == normalizedSourceKey
            && candidate.SourceRunId == normalizedSourceRunId
            && candidate.ModelKey == normalizedModelKey,
            cancellationToken);

        AddOrUpdate(provenance, hostType, hostId, normalizedFieldKey, valueJson, normalizedSourceKey, normalizedSourceRunId, normalizedModelKey, confidence);
    }

    public async Task RecordForHostsAsync(
        string fieldKey,
        IReadOnlyList<FieldProvenanceHostValue> entries,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
            return;

        var normalizedFieldKey = fieldKey.Trim().ToLowerInvariant();
        var known = new Dictionary<(AffinityHostType HostType, int HostId, string SourceKey, string SourceRunId, string ModelKey), FieldProvenance>();

        // One query per host type loads every existing row the batch can touch; rows already tracked in this
        // unit of work win, exactly as the Local-first lookup in RecordAsync.
        foreach (var group in entries.Where(entry => entry.HostId > 0).GroupBy(entry => entry.HostType))
        {
            var hostType = group.Key;
            var hostIds = group.Select(entry => entry.HostId).Distinct().ToArray();
            var existing = await db.FieldProvenance
                .Where(candidate => candidate.HostType == hostType
                    && hostIds.Contains(candidate.HostId)
                    && candidate.FieldKey == normalizedFieldKey)
                .ToListAsync(cancellationToken);
            foreach (var row in existing)
                known[(row.HostType, row.HostId, row.SourceKey, row.SourceRunId, row.ModelKey)] = row;
        }

        foreach (var entry in db.ChangeTracker.Entries<FieldProvenance>())
        {
            var row = entry.Entity;
            if (entry.State is not (EntityState.Deleted or EntityState.Detached) && row.FieldKey == normalizedFieldKey)
                known[(row.HostType, row.HostId, row.SourceKey, row.SourceRunId, row.ModelKey)] = row;
        }

        foreach (var entry in entries)
        {
            if (entry.HostId <= 0)
                continue;

            var normalizedSourceKey = NormalizeSourceKey(entry.SourceKey);
            var normalizedSourceRunId = NormalizeOptional(entry.SourceRunId);
            var key = (entry.HostType, entry.HostId, normalizedSourceKey, normalizedSourceRunId, string.Empty);
            known.TryGetValue(key, out var provenance);
            known[key] = AddOrUpdate(provenance, entry.HostType, entry.HostId, normalizedFieldKey, SerializeValue(entry.Value), normalizedSourceKey, normalizedSourceRunId, string.Empty, entry.Confidence);
        }
    }

    private FieldProvenance AddOrUpdate(
        FieldProvenance? provenance,
        AffinityHostType hostType,
        int hostId,
        string normalizedFieldKey,
        string? valueJson,
        string normalizedSourceKey,
        string normalizedSourceRunId,
        string normalizedModelKey,
        float? confidence)
    {
        if (provenance == null)
        {
            provenance = new FieldProvenance
            {
                HostType = hostType,
                HostId = hostId,
                FieldKey = normalizedFieldKey,
                ValueJson = valueJson,
                SourceKey = normalizedSourceKey,
                SourceRunId = normalizedSourceRunId,
                ModelKey = normalizedModelKey,
                Confidence = confidence,
            };
            db.FieldProvenance.Add(provenance);
            return provenance;
        }

        provenance.ValueJson = valueJson;
        if (confidence.HasValue && (!provenance.Confidence.HasValue || confidence.Value > provenance.Confidence.Value))
            provenance.Confidence = confidence.Value;
        return provenance;
    }

    public async Task RecordManyAsync(
        AffinityHostType hostType,
        int hostId,
        IReadOnlyDictionary<string, object?> fields,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var (fieldKey, value) in fields)
            await RecordAsync(hostType, hostId, fieldKey, value, sourceKey, sourceRunId, modelKey, confidence, cancellationToken);
    }

    public async Task<IReadOnlyList<FieldProvenanceDto>> GetForHostAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.FieldProvenance
            .AsNoTracking()
            .Where(item => item.HostType == hostType && item.HostId == hostId)
            .OrderBy(item => item.FieldKey)
            .ThenBy(item => item.SourceKey)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(MapToDto).ToList();
    }

    private static string? SerializeValue(object? value)
    {
        if (value == null)
            return null;

        if (value is JsonElement element)
            return element.GetRawText();

        return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
    }

    private static JsonElement? ParseValue(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return null;

        using var document = JsonDocument.Parse(valueJson);
        return document.RootElement.Clone();
    }

    private static string NormalizeSourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "user";

        var trimmed = value.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "scraper" => "scraper:local",
            "metadata" => "metadata:default",
            "import:stash" => "stash-import",
            _ => trimmed,
        };
    }

    private static string NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static FieldProvenanceDto MapToDto(FieldProvenance provenance)
        => new(
            provenance.FieldKey,
            provenance.SourceKey,
            string.IsNullOrWhiteSpace(provenance.SourceRunId) ? null : provenance.SourceRunId,
            string.IsNullOrWhiteSpace(provenance.ModelKey) ? null : provenance.ModelKey,
            ParseValue(provenance.ValueJson),
            provenance.Confidence,
            provenance.CreatedAt.ToString("o"));
}