using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class CustomFieldDefinitionLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("PUT", "/api/custom-fields/{id:int}")]
    public async Task GivenMessyDefinitionInput_WhenOwnerCreatesAndUpdates_ThenNormalizationPermissionsAndPersistenceAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var keyInput = $"  Field ++ {suffix}  ";
        var normalizedKey = $"field_{suffix}";
        var create = new CustomFieldDefinitionCreateDto
        {
            Key = keyInput,
            Label = "  Original label  ",
            Type = "ENUM",
            EntityTypes = [" Video ", "VIDEO", "unknown", " Image "],
            Options = [" Alpha ", "alpha", "Beta", " "],
            Filterable = false,
            Sortable = true,
            IsMultiValue = true,
        };

        Func<Task> forbiddenCreate = () => member.CreateCustomFieldDefinitionAsync(create);
        await forbiddenCreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEmpty();

        var created = await owner.CreateCustomFieldDefinitionAsync(create, TestContext.Current.CancellationToken);
        AssertDefinition(
            created,
            created.Id,
            normalizedKey,
            "Original label",
            "enum",
            ["video", "image"],
            ["Alpha", "Beta"],
            filterable: false,
            sortable: true,
            isMultiValue: true,
            displayOrder: 0);
        created.CreatedAt.Should().NotBeNullOrWhiteSpace();
        created.UpdatedAt.Should().NotBeNullOrWhiteSpace();

        Func<Task> duplicateCreate = () => owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = normalizedKey.ToUpperInvariant(),
            Label = "Duplicate",
            EntityTypes = ["video"],
        });
        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*already exists*");
        Func<Task> invalidEntityTypes = () => owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = $"invalid_{suffix}",
            Label = "Invalid entities",
            EntityTypes = ["unknown", " "],
        });
        await invalidEntityTypes.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*Select at least one entity type*");

        Func<Task> forbiddenUpdate = () => member.UpdateCustomFieldDefinitionAsync(
            created.Id,
            new CustomFieldDefinitionUpdateDto { Label = "Forbidden label" });
        await forbiddenUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var afterForbidden = (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        AssertDefinition(
            afterForbidden,
            created.Id,
            normalizedKey,
            "Original label",
            "enum",
            ["video", "image"],
            ["Alpha", "Beta"],
            filterable: false,
            sortable: true,
            isMultiValue: true,
            displayOrder: 0);

        var updatedKey = $"updated_{suffix}";
        var updated = await owner.UpdateCustomFieldDefinitionAsync(created.Id, new CustomFieldDefinitionUpdateDto
        {
            Key = $"  UPDATED -- {suffix}  ",
            Label = "  Updated label  ",
            Options = [" Gamma ", "gamma", "Delta"],
            Filterable = true,
            Sortable = false,
            DisplayOrder = 17,
        }, TestContext.Current.CancellationToken);
        AssertDefinition(
            updated,
            created.Id,
            updatedKey,
            "Updated label",
            "enum",
            ["video", "image"],
            ["Gamma", "Delta"],
            filterable: true,
            sortable: false,
            isMultiValue: true,
            displayOrder: 17);
        updated.CreatedAt.Should().Be(afterForbidden.CreatedAt);
        DateTimeOffset.Parse(updated.UpdatedAt!).Should().BeAfter(
            DateTimeOffset.Parse(afterForbidden.UpdatedAt!));

        Func<Task> missingUpdate = () => owner.UpdateCustomFieldDefinitionAsync(
            int.MaxValue,
            new CustomFieldDefinitionUpdateDto { Label = "Missing" });
        await missingUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        var persisted = (await owner.GetCustomFieldDefinitionsAsync(" VIDEO ", TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        AssertDefinition(
            persisted,
            created.Id,
            updatedKey,
            "Updated label",
            "enum",
            ["video", "image"],
            ["Gamma", "Delta"],
            filterable: true,
            sortable: false,
            isMultiValue: true,
            displayOrder: 17);
        persisted.CreatedAt.Should().Be(afterForbidden.CreatedAt);
        DateTimeOffset.Parse(persisted.UpdatedAt!).Should().BeCloseTo(
            DateTimeOffset.Parse(updated.UpdatedAt!),
            TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/custom-fields")]
    [CoversEndpoint("DELETE", "/api/custom-fields/{id:int}")]
    public async Task GivenValueBearingDefinitions_WhenOwnerReplacesAndDeletes_ThenOrderingCascadesAndIsolationAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var retainedKey = $"retained_{suffix}";
        var renamedKey = $"renamed_{suffix}";
        var omittedKey = $"omitted_{suffix}";
        var controlKey = $"control_{suffix}";
        var newKey = $"new_{suffix}";
        var retained = await CreateTextDefinitionAsync(owner, retainedKey, "Retained", displayOrder: 10);
        var omitted = await CreateTextDefinitionAsync(owner, omittedKey, "Omitted", displayOrder: 20);
        var control = await CreateTextDefinitionAsync(owner, controlKey, "Control", displayOrder: 30);
        var video = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Custom field lifecycle {suffix}")
            .WithCustomField(retainedKey, "retained value")
            .WithCustomField(omittedKey, "omitted value")
            .WithCustomField(controlKey, "control value")
            .Build(), TestContext.Current.CancellationToken);
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [
                (retainedKey, "retained value"),
                (omittedKey, "omitted value"),
                (controlKey, "control value"),
            ]);

        var replacement = new List<CustomFieldDefinitionSyncDto>
        {
            new()
            {
                Key = newKey,
                Label = "New definition",
                Type = "text",
                EntityTypes = ["video"],
                DisplayOrder = null,
            },
            new()
            {
                Id = retained.Id,
                Key = renamedKey,
                Label = "Renamed retained",
                Type = "text",
                EntityTypes = ["video"],
                DisplayOrder = 20,
            },
            new()
            {
                Id = control.Id,
                Key = controlKey,
                Label = "Control",
                Type = "text",
                EntityTypes = ["video"],
                DisplayOrder = 30,
            },
        };
        var beforeForbiddenReplace = await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Func<Task> forbiddenReplace = () => member.ReplaceCustomFieldDefinitionsAsync(replacement);
        await forbiddenReplace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(
            beforeForbiddenReplace,
            options => options.WithStrictOrdering());
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [
                (retainedKey, "retained value"),
                (omittedKey, "omitted value"),
                (controlKey, "control value"),
            ]);

        var replaced = await owner.ReplaceCustomFieldDefinitionsAsync(replacement, TestContext.Current.CancellationToken);
        replaced.Select(definition => definition.Key).Should().Equal(newKey, renamedKey, controlKey);
        replaced.Select(definition => definition.DisplayOrder).Should().Equal(0, 20, 30);
        var createdByReplace = replaced[0];
        createdByReplace.Id.Should().BePositive();
        replaced[1].Id.Should().Be(retained.Id);
        replaced[2].Id.Should().Be(control.Id);
        replaced.Should().NotContain(definition => definition.Id == omitted.Id || definition.Key == omittedKey);
        (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(
            replaced,
            options => options.WithStrictOrdering());
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [(renamedKey, "retained value"), (controlKey, "control value")]);

        var duplicateReplacement = new List<CustomFieldDefinitionSyncDto>
        {
            ToSync(createdByReplace, " Duplicate key "),
            ToSync(replaced[1], "duplicate-key"),
            ToSync(replaced[2], controlKey),
        };
        Func<Task> duplicateKeys = () => owner.ReplaceCustomFieldDefinitionsAsync(duplicateReplacement);
        await duplicateKeys.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*already exists*");
        (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(
            replaced,
            options => options.WithStrictOrdering());
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [(renamedKey, "retained value"), (controlKey, "control value")]);

        await owner.UpdateVideoAsync(video.Id, new
        {
            customFields = new Dictionary<string, object>
            {
                [renamedKey] = "retained value",
                [controlKey] = "control value",
                [newKey] = "new value",
            },
        }, TestContext.Current.CancellationToken);
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [(newKey, "new value"), (renamedKey, "retained value"), (controlKey, "control value")]);

        Func<Task> forbiddenDelete = () => member.DeleteCustomFieldDefinitionAsync(createdByReplace.Id);
        await forbiddenDelete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [(newKey, "new value"), (renamedKey, "retained value"), (controlKey, "control value")]);

        await owner.DeleteCustomFieldDefinitionAsync(createdByReplace.Id, TestContext.Current.CancellationToken);
        var afterDelete = await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken);
        afterDelete.Select(definition => definition.Id).Should().Equal(retained.Id, control.Id);
        afterDelete.Select(definition => definition.Key).Should().Equal(renamedKey, controlKey);
        AssertTextCustomFields(
            await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken),
            [(renamedKey, "retained value"), (controlKey, "control value")]);
        Func<Task> repeatedDelete = () => owner.DeleteCustomFieldDefinitionAsync(createdByReplace.Id);
        await repeatedDelete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    private static Task<CustomFieldDefinitionDto> CreateTextDefinitionAsync(
        CoveClient client,
        string key,
        string label,
        int displayOrder)
        => client.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = label,
            Type = "text",
            EntityTypes = ["video"],
            DisplayOrder = displayOrder,
        });

    private static CustomFieldDefinitionSyncDto ToSync(CustomFieldDefinitionDto definition, string key)
        => new()
        {
            Id = definition.Id,
            Key = key,
            Label = definition.Label,
            Type = definition.Type,
            EntityTypes = definition.EntityTypes,
            Options = definition.Options,
            Filterable = definition.Filterable,
            Sortable = definition.Sortable,
            IsMultiValue = definition.IsMultiValue,
            DisplayOrder = definition.DisplayOrder,
        };

    private static void AssertDefinition(
        CustomFieldDefinitionDto actual,
        int expectedId,
        string expectedKey,
        string expectedLabel,
        string expectedType,
        IReadOnlyList<string> expectedEntityTypes,
        IReadOnlyList<string> expectedOptions,
        bool filterable,
        bool sortable,
        bool isMultiValue,
        int displayOrder)
    {
        actual.Id.Should().Be(expectedId);
        actual.Key.Should().Be(expectedKey);
        actual.Label.Should().Be(expectedLabel);
        actual.Type.Should().Be(expectedType);
        actual.EntityTypes.Should().Equal(expectedEntityTypes);
        actual.Options.Should().Equal(expectedOptions);
        actual.Filterable.Should().Be(filterable);
        actual.Sortable.Should().Be(sortable);
        actual.IsMultiValue.Should().Be(isMultiValue);
        actual.DisplayOrder.Should().Be(displayOrder);
    }

    private static void AssertTextCustomFields(
        VideoDto video,
        params (string Key, string Value)[] expected)
    {
        video.CustomFields.Should().NotBeNull();
        video.CustomFields.Should().HaveCount(expected.Length);
        foreach (var (key, value) in expected)
        {
            video.CustomFields.Should().ContainKey(key)
                .WhoseValue.Should().BeOfType<JsonElement>()
                .Which.GetString().Should().Be(value);
        }
    }
}
