using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class CustomFieldRepositoryTests
{
    [Fact]
    public async Task FindOrCreateDefinitionAsync_LongTextNormalizesExtensionDefinitionAndUsesLongTextStorage()
    {
        await using var context = CreateContext();
        var repository = new CustomFieldRepository(context);
        var value = $"Short values work too.\n\n{new string('x', 5_001)}";

        var definition = await repository.FindOrCreateDefinitionAsync(new CustomFieldDefinition
        {
            Key = "extension_notes",
            Label = "Extension notes",
            Type = "LONGTEXT",
            EntityTypes = [CustomFieldEntityTypes.Performer],
            Filterable = true,
            Sortable = true,
            IsMultiValue = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(CustomFieldTypes.LongText, definition.Type);
        Assert.False(definition.Filterable);
        Assert.False(definition.Sortable);
        Assert.False(definition.IsMultiValue);

        await repository.UpsertValueAsync(
            CustomFieldEntityTypes.Performer,
            42,
            definition.Id,
            value,
            TestContext.Current.CancellationToken);

        var persistedDefinition = await context.CustomFieldDefinitions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CustomFieldTypes.LongText, persistedDefinition.Type);
        Assert.False(persistedDefinition.Filterable);
        Assert.False(persistedDefinition.Sortable);
        Assert.False(persistedDefinition.IsMultiValue);
        var persistedValue = await context.CustomFieldValues.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(persistedValue.TextValue);
        Assert.Equal(value, persistedValue.LongTextValue);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"custom-field-repository-{Guid.NewGuid():N}")
            .Options;
        return new CoveContext(options);
    }
}
