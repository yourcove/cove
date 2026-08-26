using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class CustomFieldServiceTests
{
    [Fact]
    public async Task CreateDefinitionAsync_NormalizesJsonAndDisablesUnsupportedBehaviors()
    {
        await using var context = CreateContext();
        var service = new CustomFieldService(context);

        var definition = await service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "structured_metadata",
            Label = "Structured Metadata",
            Type = "JSON",
            EntityTypes = [CustomFieldEntityTypes.Video],
            Filterable = true,
            Sortable = true,
            IsMultiValue = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(CustomFieldTypes.Json, definition.Type);
        Assert.False(definition.Filterable);
        Assert.False(definition.Sortable);
        Assert.False(definition.IsMultiValue);
    }

    [Theory]
    [InlineData("{\"profile\":{\"score\":0.95,\"reviewed\":true},\"labels\":[\"one\",\"two\"]}")]
    [InlineData("[{\"path\":\"first\"},{\"path\":\"second\"}]")]
    [InlineData("\"scalar\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task SaveValuesAsync_JsonRoundTripsAsOneStructuredValue(string json)
    {
        await using var context = CreateContext();
        var definition = new CustomFieldDefinition
        {
            Key = "structured_metadata",
            Label = "Structured Metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
        };
        context.CustomFieldDefinitions.Add(definition);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var service = new CustomFieldService(context);
        await service.SaveValuesAsync(
            CustomFieldEntityTypes.Video,
            42,
            new Dictionary<string, object> { [definition.Key] = document.RootElement.Clone() },
            TestContext.Current.CancellationToken);

        var stored = await context.CustomFieldValues.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(stored.TextValue);
        using var storedDocument = JsonDocument.Parse(Assert.IsType<string>(stored.JsonValue));
        Assert.True(JsonElement.DeepEquals(document.RootElement, storedDocument.RootElement));

        var values = await service.GetValuesAsync(CustomFieldEntityTypes.Video, 42, TestContext.Current.CancellationToken);
        var actual = Assert.IsType<JsonElement>(values[definition.Key]);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual));
    }

    [Fact]
    public async Task SaveValuesAsync_JsonNullRemovesTheCustomFieldValue()
    {
        await using var context = CreateContext();
        var definition = new CustomFieldDefinition
        {
            Key = "structured_metadata",
            Label = "Structured Metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
        };
        context.CustomFieldDefinitions.Add(definition);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new CustomFieldService(context);
        using var initialDocument = JsonDocument.Parse("{\"present\":true}");
        await service.SaveValuesAsync(
            CustomFieldEntityTypes.Video,
            42,
            new Dictionary<string, object> { [definition.Key] = initialDocument.RootElement.Clone() },
            TestContext.Current.CancellationToken);
        using var nullDocument = JsonDocument.Parse("null");

        await service.SaveValuesAsync(
            CustomFieldEntityTypes.Video,
            42,
            new Dictionary<string, object> { [definition.Key] = nullDocument.RootElement.Clone() },
            TestContext.Current.CancellationToken);

        Assert.Empty(await context.CustomFieldValues.ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            definition.Key,
            await service.GetValuesAsync(CustomFieldEntityTypes.Video, 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceDefinitionsAsync_CreatesUpdatesDeletesAndSupportsAudioAndTextEntities()
    {
        await using var context = CreateContext();
        context.CustomFieldDefinitions.AddRange(
            new CustomFieldDefinition
            {
                Key = "legacy_code",
                Label = "Legacy Code",
                Type = CustomFieldTypes.Text,
                EntityTypes = [CustomFieldEntityTypes.Video],
                DisplayOrder = 0,
            },
            new CustomFieldDefinition
            {
                Key = "performer_code",
                Label = "Performer Code",
                Type = CustomFieldTypes.Text,
                EntityTypes = [CustomFieldEntityTypes.Performer],
                DisplayOrder = 10,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new CustomFieldService(context);
        var existingDefinitions = await service.GetDefinitionsAsync(ct: CancellationToken.None);
        var performerCodeDefinition = existingDefinitions.Single(definition => definition.Key == "performer_code");

        var syncedDefinitions = await service.ReplaceDefinitionsAsync(
        [
            new CustomFieldDefinitionSyncDto
            {
                Id = performerCodeDefinition.Id,
                Key = "performer_code",
                Label = "Performer Code Updated",
                Type = CustomFieldTypes.Text,
                EntityTypes = [CustomFieldEntityTypes.Performer, CustomFieldEntityTypes.Audio, CustomFieldEntityTypes.Text],
                Options = [],
                Filterable = true,
                Sortable = true,
                IsMultiValue = false,
                DisplayOrder = 0,
            },
            new CustomFieldDefinitionSyncDto
            {
                Key = "listening_score",
                Label = "Listening Score",
                Type = CustomFieldTypes.Number,
                EntityTypes = [CustomFieldEntityTypes.Audio, CustomFieldEntityTypes.Text],
                Options = [],
                Filterable = true,
                Sortable = false,
                IsMultiValue = false,
                DisplayOrder = 10,
            },
        ],
        CancellationToken.None);

        Assert.Equal(["performer_code", "listening_score"], syncedDefinitions.Select(definition => definition.Key).ToArray());

        var updatedDefinition = syncedDefinitions[0];
        Assert.Equal("Performer Code Updated", updatedDefinition.Label);
        Assert.Equal([CustomFieldEntityTypes.Performer, CustomFieldEntityTypes.Audio, CustomFieldEntityTypes.Text], updatedDefinition.EntityTypes);
        Assert.True(updatedDefinition.Sortable);

        var createdDefinition = syncedDefinitions[1];
        Assert.True(createdDefinition.Id > 0);
        Assert.Equal([CustomFieldEntityTypes.Audio, CustomFieldEntityTypes.Text], createdDefinition.EntityTypes);

        var persistedDefinitions = await context.CustomFieldDefinitions
            .OrderBy(definition => definition.DisplayOrder)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, persistedDefinitions.Count);
        Assert.DoesNotContain(persistedDefinitions, definition => definition.Key == "legacy_code");
        Assert.Contains(persistedDefinitions, definition => definition.Key == "listening_score");
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"custom-field-service-{Guid.NewGuid():N}")
            .Options;
        return new CoveContext(options);
    }
}
