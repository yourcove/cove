using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class FieldProvenanceService(CoveContext db) : IFieldProvenanceService
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

        if (provenance == null)
        {
            db.FieldProvenance.Add(new FieldProvenance
            {
                HostType = hostType,
                HostId = hostId,
                FieldKey = normalizedFieldKey,
                ValueJson = valueJson,
                SourceKey = normalizedSourceKey,
                SourceRunId = normalizedSourceRunId,
                ModelKey = normalizedModelKey,
                Confidence = confidence,
            });
            return;
        }

        provenance.ValueJson = valueJson;
        if (confidence.HasValue && (!provenance.Confidence.HasValue || confidence.Value > provenance.Confidence.Value))
            provenance.Confidence = confidence.Value;
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