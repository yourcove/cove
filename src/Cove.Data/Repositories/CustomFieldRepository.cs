using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class CustomFieldRepository : ICustomFieldRepository
{
    private readonly CoveContext _db;
    public CustomFieldRepository(CoveContext db) => _db = db;

    public async Task<CustomFieldDefinition?> FindDefinitionAsync(string entityType, string key, CancellationToken ct = default)
    {
        var definition = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.EntityTypes.Contains(entityType) && d.Key == key, ct);
        if (definition != null)
            NormalizeDefinitionCapabilities(definition);
        return definition;
    }

    public async Task<CustomFieldDefinition> FindOrCreateDefinitionAsync(CustomFieldDefinition definition, CancellationToken ct = default)
    {
        NormalizeDefinitionCapabilities(definition);
        var existing = await _db.CustomFieldDefinitions
            .FirstOrDefaultAsync(d => d.Key == definition.Key
                && d.EntityTypes.Contains(definition.EntityTypes.FirstOrDefault() ?? string.Empty), ct);

        if (existing != null)
        {
            if (NormalizeDefinitionCapabilities(existing))
                await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.CustomFieldDefinitions.Add(definition);
        await _db.SaveChangesAsync(ct);
        return definition;
    }

    public async Task<IReadOnlyList<CustomFieldValue>> FindValuesAsync(string entityType, int entityId, CancellationToken ct = default)
    {
        return await _db.CustomFieldValues
            .Where(v => v.EntityType == entityType && v.EntityId == entityId)
            .Include(v => v.Definition)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task UpsertValueAsync(string entityType, int entityId, int definitionId, string value, CancellationToken ct = default)
    {
        var definitionType = await _db.CustomFieldDefinitions
            .Where(definition => definition.Id == definitionId)
            .Select(definition => definition.Type)
            .SingleAsync(ct);
        var isLongText = CustomFieldTypes.IsLongText(definitionType);
        var existing = await _db.CustomFieldValues
            .FirstOrDefaultAsync(v => v.EntityType == entityType && v.EntityId == entityId && v.DefinitionId == definitionId, ct);

        if (existing != null)
        {
            existing.TextValue = isLongText ? null : value;
            existing.LongTextValue = isLongText ? value : null;
            _db.CustomFieldValues.Update(existing);
        }
        else
        {
            _db.CustomFieldValues.Add(new CustomFieldValue
            {
                EntityType = entityType,
                EntityId = entityId,
                DefinitionId = definitionId,
                TextValue = isLongText ? null : value,
                LongTextValue = isLongText ? value : null,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertNumberValueAsync(string entityType, int entityId, int definitionId, decimal value, CancellationToken ct = default)
    {
        var existing = await _db.CustomFieldValues
            .Where(v => v.EntityType == entityType && v.EntityId == entityId && v.DefinitionId == definitionId)
            .ToListAsync(ct);
        _db.CustomFieldValues.RemoveRange(existing);
        _db.CustomFieldValues.Add(new CustomFieldValue
        {
            DefinitionId = definitionId,
            EntityType = entityType,
            EntityId = entityId,
            NumberValue = value,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<decimal?> FindNumberValueAsync(string entityType, int entityId, string definitionKey, CancellationToken ct = default)
        => await _db.CustomFieldValues
            .AsNoTracking()
            .Include(v => v.Definition)
            .Where(v => v.EntityType == entityType && v.EntityId == entityId
                && v.Definition != null && v.Definition.Key == definitionKey)
            .OrderBy(v => v.Position)
            .Select(v => v.NumberValue)
            .FirstOrDefaultAsync(ct);

    private static bool NormalizeDefinitionCapabilities(CustomFieldDefinition definition)
    {
        var type = CustomFieldTypes.Normalize(definition.Type);
        var isNonQueryableScalar = CustomFieldTypes.IsJson(type) || CustomFieldTypes.IsLongText(type);
        var changed = !string.Equals(definition.Type, type, StringComparison.Ordinal)
            || (isNonQueryableScalar && (definition.Filterable || definition.Sortable || definition.IsMultiValue));
        definition.Type = type;
        if (isNonQueryableScalar)
        {
            definition.Filterable = false;
            definition.Sortable = false;
            definition.IsMultiValue = false;
        }

        return changed;
    }
}
