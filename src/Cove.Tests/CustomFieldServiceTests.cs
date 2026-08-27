using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class CustomFieldServiceTests
{
    [Fact]
    public async Task CreateDefinitionAsync_NormalizesLongTextAndDisablesQueryBehaviors()
    {
        await using var context = CreateContext();
        var service = new CustomFieldService(context);

        var definition = await service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "notes",
            Label = "Notes",
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
    }

    [Fact]
    public async Task SaveValuesAsync_LongTextPreservesShortAndLargeMultilineScalarsOutsideTextValue()
    {
        await using var context = CreateContext();
        var definition = new CustomFieldDefinition
        {
            Key = "notes",
            Label = "Notes",
            Type = CustomFieldTypes.LongText,
            EntityTypes = [CustomFieldEntityTypes.Performer],
        };
        context.CustomFieldDefinitions.Add(definition);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new CustomFieldService(context);
        var shortValue = "Short values work too.";
        var largeValue = $"Opening line\n\n{new string('x', 5_001)}\nClosing line";

        await service.SaveValuesAsync(
            CustomFieldEntityTypes.Performer,
            41,
            new Dictionary<string, object> { [definition.Key] = shortValue },
            TestContext.Current.CancellationToken);
        await service.SaveValuesAsync(
            CustomFieldEntityTypes.Performer,
            42,
            new Dictionary<string, object> { [definition.Key] = largeValue },
            TestContext.Current.CancellationToken);

        var stored = await context.CustomFieldValues.OrderBy(value => value.EntityId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);
        Assert.Equal(shortValue, stored[0].LongTextValue);
        Assert.Equal(largeValue, stored[1].LongTextValue);
        Assert.All(stored, value => Assert.Null(value.TextValue));
        var roundTripped = await service.GetValuesAsync(
            CustomFieldEntityTypes.Performer,
            [41, 42],
            TestContext.Current.CancellationToken);
        Assert.Equal(shortValue, roundTripped[41][definition.Key]);
        Assert.Equal(largeValue, roundTripped[42][definition.Key]);
    }

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
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto
                {
                    Path = "/profile/score",
                    Label = " Score ",
                    Type = "NUMBER",
                    Filterable = true,
                    Sortable = true,
                },
            ],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(CustomFieldTypes.Json, definition.Type);
        Assert.False(definition.Filterable);
        Assert.False(definition.Sortable);
        Assert.False(definition.IsMultiValue);
        var jsonPath = Assert.Single(definition.JsonPaths);
        Assert.Equal("/profile/score", jsonPath.Path);
        Assert.Equal("Score", jsonPath.Label);
        Assert.Equal(CustomFieldTypes.Number, jsonPath.Type);
        Assert.True(jsonPath.Filterable);
        Assert.True(jsonPath.Sortable);
    }

    [Fact]
    public async Task CreateDefinitionAsync_RejectsInvalidOrDuplicateJsonPointers()
    {
        await using var context = CreateContext();
        var service = new CustomFieldService(context);

        var invalidPath = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "invalid_json_path",
            Label = "Invalid JSON path",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths = [new CustomFieldJsonPathDefinitionDto { Path = "profile.score", Label = "Score", Type = CustomFieldTypes.Number }],
        }, TestContext.Current.CancellationToken));
        Assert.Contains("JSON Pointer", invalidPath.Message, StringComparison.Ordinal);

        var duplicatePath = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "duplicate_json_path",
            Label = "Duplicate JSON path",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto { Path = "/profile/score", Label = "Score", Type = CustomFieldTypes.Number },
                new CustomFieldJsonPathDefinitionDto { Path = "/profile/score", Label = "Score again", Type = CustomFieldTypes.Number },
            ],
        }, TestContext.Current.CancellationToken));
        Assert.Contains("Duplicate JSON Pointer", duplicatePath.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDefinitionAsync_PreservesPointerWhitespaceAndRejectsInvalidPathMetadata()
    {
        await using var context = CreateContext();
        var service = new CustomFieldService(context);

        var definition = await service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "whitespace_json_path",
            Label = "Whitespace JSON path",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths = [new CustomFieldJsonPathDefinitionDto { Path = "/profile/score ", Label = "Score", Type = "NUMBER" }],
        }, TestContext.Current.CancellationToken);
        Assert.Equal("/profile/score ", Assert.Single(definition.JsonPaths).Path);

        var invalidType = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "invalid_json_path_type",
            Label = "Invalid JSON path type",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths = [new CustomFieldJsonPathDefinitionDto { Path = "/profile/score", Label = "Score", Type = "numer" }],
        }, TestContext.Current.CancellationToken));
        Assert.Contains("text, number, and boolean", invalidType.Message, StringComparison.Ordinal);

        var longLabel = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = "long_json_path_label",
            Label = "Long JSON path label",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths = [new CustomFieldJsonPathDefinitionDto { Path = "/profile/score", Label = new string('x', 201), Type = CustomFieldTypes.Number }],
        }, TestContext.Current.CancellationToken));
        Assert.Contains("200 characters", longLabel.Message, StringComparison.Ordinal);
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
        Assert.True(stored.JsonValue.HasValue);
        Assert.True(JsonElement.DeepEquals(document.RootElement, stored.JsonValue.Value));

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
    public async Task SqliteFallback_JsonValueRoundTripsNullAndPersistsReplacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;

        await using (var context = new CoveContext(options))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var definition = new CustomFieldDefinition
            {
                Key = "structured_metadata",
                Label = "Structured Metadata",
                Type = CustomFieldTypes.Json,
                EntityTypes = [CustomFieldEntityTypes.Video],
            };
            using var initial = JsonDocument.Parse("{\"score\":0.01}");
            using var jsonNull = JsonDocument.Parse("null");
            context.CustomFieldValues.AddRange(
                new CustomFieldValue
                {
                    Definition = definition,
                    EntityType = CustomFieldEntityTypes.Video,
                    EntityId = 41,
                    JsonValue = initial.RootElement.Clone(),
                },
                new CustomFieldValue
                {
                    Definition = definition,
                    EntityType = CustomFieldEntityTypes.Video,
                    EntityId = 42,
                    JsonValue = jsonNull.RootElement.Clone(),
                },
                new CustomFieldValue
                {
                    Definition = definition,
                    EntityType = CustomFieldEntityTypes.Video,
                    EntityId = 43,
                    JsonValue = null,
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = new CoveContext(options))
        {
            var values = await context.CustomFieldValues.OrderBy(value => value.EntityId).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal("{\"score\":0.01}", values[0].JsonValue?.GetRawText());
            Assert.Equal(JsonValueKind.Null, values[1].JsonValue?.ValueKind);
            Assert.Null(values[2].JsonValue);

            using var replacement = JsonDocument.Parse("{\"score\":0.02}");
            values[0].JsonValue = replacement.RootElement.Clone();
            Assert.True(context.ChangeTracker.HasChanges());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = new CoveContext(options))
        {
            var replaced = await context.CustomFieldValues.SingleAsync(value => value.EntityId == 41, TestContext.Current.CancellationToken);
            Assert.Equal("{\"score\":0.02}", replaced.JsonValue?.GetRawText());
        }
    }

    [Theory]
    [InlineData(CriterionModifier.NotNull, 41)]
    [InlineData(CriterionModifier.IsNull, 42)]
    public async Task JsonCustomFieldPresenceFilter_DoesNotRequireAConfiguredPath(CriterionModifier modifier, int expectedEntityId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var definition = new CustomFieldDefinition
        {
            Key = "structured_metadata",
            Label = "Structured Metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
        };
        using var document = JsonDocument.Parse("{\"present\":true}");
        context.Videos.AddRange(
            new Video { Id = 41, Title = "Has structured metadata" },
            new Video { Id = 42, Title = "No structured metadata" });
        context.CustomFieldValues.Add(new CustomFieldValue
        {
            Definition = definition,
            EntityType = CustomFieldEntityTypes.Video,
            EntityId = 41,
            JsonValue = document.RootElement.Clone(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ids = await context.Videos
            .ApplyCustomFieldCriterion(context, CustomFieldEntityTypes.Video, new CustomFieldCriterion
            {
                Key = definition.Key,
                Type = CustomFieldTypes.Json,
                Modifier = modifier,
            })
            .Select(video => video.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([expectedEntityId], ids);
        Assert.Empty(definition.JsonPaths);
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
