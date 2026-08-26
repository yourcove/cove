using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class CustomFieldServiceTests
{
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
