using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class CustomFieldDefinitionLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("POST", "/api/videos")]
    [CoversEndpoint("GET", "/api/videos/{id:int}")]
    public async Task GivenJsonDefinition_WhenValueIsSavedAndReloaded_ThenStructuredJsonRoundTripsWithoutTextLimits()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"structured_{suffix}";
        var definition = await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "Structured metadata",
            Type = "JSON",
            EntityTypes = ["video"],
            Filterable = true,
            Sortable = true,
            IsMultiValue = true,
        }, TestContext.Current.CancellationToken);
        AssertDefinition(
            definition,
            definition.Id,
            key,
            "Structured metadata",
            "json",
            ["video"],
            [],
            filterable: false,
            sortable: false,
            isMultiValue: false,
            displayOrder: 0);

        var expected = JsonSerializer.SerializeToElement(new
        {
            profile = new { score = 0.95m, reviewed = true },
            labels = new[] { "one", "two" },
            notes = new string('x', 5_001),
        });
        var created = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON custom field {suffix}")
            .WithCustomField(key, expected)
            .Build(), TestContext.Current.CancellationToken);

        AssertJsonCustomField(created, key, expected);
        AssertJsonCustomField(
            await owner.GetVideoByIdAsync(created.Id, TestContext.Current.CancellationToken),
            key,
            expected);
    }

    [Theory]
    [InlineData("[{\"path\":\"first\"},{\"path\":\"second\"}]")]
    [InlineData("\"scalar\"")]
    [InlineData("42")]
    [InlineData("true")]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("POST", "/api/videos")]
    [CoversEndpoint("GET", "/api/videos/{id:int}")]
    public async Task GivenJsonDefinition_WhenRootJsonValueIsSaved_ThenItsJsonTypeIsPreserved(string json)
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"json_root_{suffix}";
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "JSON root value",
            Type = "json",
            EntityTypes = ["video"],
        }, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var expected = document.RootElement.Clone();

        var created = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON root custom field {suffix}")
            .WithCustomField(key, expected)
            .Build(), TestContext.Current.CancellationToken);

        AssertJsonCustomField(created, key, expected);
        AssertJsonCustomField(
            await owner.GetVideoByIdAsync(created.Id, TestContext.Current.CancellationToken),
            key,
            expected);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("PUT", "/api/custom-fields/{id:int}")]
    [CoversEndpoint("POST", "/api/videos")]
    [CoversEndpoint("POST", "/api/videos/find")]
    [CoversEndpoint("POST", "/api/audios")]
    [CoversEndpoint("POST", "/api/audios/find")]
    public async Task GivenConfiguredJsonPaths_WhenDifferentEntityTypesAreFilteredAndSorted_ThenTypedJsonbValuesAreUsed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"queryable_json_{suffix}";
        const string scorePath = "/profile/score";
        var definition = await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "Queryable metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video, CustomFieldEntityTypes.Audio],
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto
                {
                    Path = scorePath,
                    Label = "Score",
                    Type = CustomFieldTypes.Number,
                    Filterable = true,
                    Sortable = true,
                },
            ],
        }, TestContext.Current.CancellationToken);
        definition.JsonPaths.Should().ContainSingle().Which.Path.Should().Be(scorePath);

        var high = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Queryable JSON high {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = 30 } }))
            .Build(), TestContext.Current.CancellationToken);
        var low = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Queryable JSON low {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = 10 } }))
            .Build(), TestContext.Current.CancellationToken);
        var middle = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Queryable JSON middle {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = 20 } }))
            .Build(), TestContext.Current.CancellationToken);
        var missing = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Queryable JSON missing {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { reviewed = true } }))
            .Build(), TestContext.Current.CancellationToken);
        var wrongType = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Queryable JSON wrong type {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = "30" } }))
            .Build(), TestContext.Current.CancellationToken);
        var ids = new[] { low.Id, middle.Id, high.Id, missing.Id, wrongType.Id };

        var filtered = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter
            {
                Ids = [.. ids],
                CustomFieldCriteria =
                [
                    new CustomFieldCriterion
                    {
                        Key = key,
                        JsonPath = scorePath,
                        Type = CustomFieldTypes.Number,
                        Modifier = CriterionModifier.GreaterThan,
                        Value = "15",
                    },
                ],
            },
            FindFilter = new FindFilter { PerPage = 10 },
        }, TestContext.Current.CancellationToken);
        filtered.Items.Select(video => video.Id).Should().BeEquivalentTo([middle.Id, high.Id]);

        var sorted = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [.. ids] },
            FindFilter = new FindFilter
            {
                PerPage = 10,
                Sort = $"custom-json:number:{key}:{Uri.EscapeDataString(scorePath)}",
                Direction = Cove.Core.Enums.SortDirection.Asc,
            },
        }, TestContext.Current.CancellationToken);
        sorted.Items.Select(video => video.Id).Should().Equal(low.Id, middle.Id, high.Id, missing.Id, wrongType.Id);

        var missingOrWrongType = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter
            {
                Ids = [.. ids],
                CustomFieldCriteria =
                [
                    new CustomFieldCriterion
                    {
                        Key = key,
                        JsonPath = scorePath,
                        Type = CustomFieldTypes.Number,
                        Modifier = CriterionModifier.IsNull,
                    },
                ],
            },
            FindFilter = new FindFilter { PerPage = 10 },
        }, TestContext.Current.CancellationToken);
        missingOrWrongType.Items.Select(video => video.Id).Should().BeEquivalentTo([missing.Id, wrongType.Id]);

        var unconfigured = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter
            {
                Ids = [.. ids],
                CustomFieldCriteria =
                [
                    new CustomFieldCriterion
                    {
                        Key = key,
                        JsonPath = "/profile/unconfigured",
                        Type = CustomFieldTypes.Number,
                        Modifier = CriterionModifier.GreaterThan,
                        Value = "0",
                    },
                ],
            },
            FindFilter = new FindFilter { PerPage = 10 },
        }, TestContext.Current.CancellationToken);
        unconfigured.Items.Should().BeEmpty();

        var audioLow = await owner.CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Queryable JSON audio low {suffix}")
            .Build() with
            {
                CustomFields = new Dictionary<string, object>
                {
                    [key] = JsonSerializer.SerializeToElement(new { profile = new { score = 40 } }),
                },
            }, TestContext.Current.CancellationToken);
        var audioHigh = await owner.CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Queryable JSON audio high {suffix}")
            .Build() with
            {
                CustomFields = new Dictionary<string, object>
                {
                    [key] = JsonSerializer.SerializeToElement(new { profile = new { score = 50 } }),
                },
            }, TestContext.Current.CancellationToken);
        var audioIds = new[] { audioLow.Id, audioHigh.Id };

        var filteredAudios = await owner.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [.. audioIds],
            ObjectFilter = new AudioFilter
            {
                CustomFieldCriteria =
                [
                    new CustomFieldCriterion
                    {
                        Key = key,
                        JsonPath = scorePath,
                        Type = CustomFieldTypes.Number,
                        Modifier = CriterionModifier.GreaterThan,
                        Value = "45",
                    },
                ],
            },
            FindFilter = new FindFilter { PerPage = 10 },
        }, TestContext.Current.CancellationToken);
        filteredAudios.Items.Select(audio => audio.Id).Should().Equal(audioHigh.Id);

        var sortedAudios = await owner.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [.. audioIds],
            ObjectFilter = new AudioFilter(),
            FindFilter = new FindFilter
            {
                PerPage = 10,
                Sort = $"custom-json:number:{key}:{Uri.EscapeDataString(scorePath)}",
                Direction = Cove.Core.Enums.SortDirection.Desc,
            },
        }, TestContext.Current.CancellationToken);
        sortedAudios.Items.Select(audio => audio.Id).Should().Equal(audioHigh.Id, audioLow.Id);

        var disabledDefinition = await owner.UpdateCustomFieldDefinitionAsync(definition.Id, new CustomFieldDefinitionUpdateDto
        {
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto
                {
                    Path = scorePath,
                    Label = "Score",
                    Type = CustomFieldTypes.Number,
                    Filterable = false,
                    Sortable = false,
                },
            ],
        }, TestContext.Current.CancellationToken);
        var disabledPath = disabledDefinition.JsonPaths.Should().ContainSingle().Which;
        disabledPath.Filterable.Should().BeFalse();
        disabledPath.Sortable.Should().BeFalse();
        var persistedDisabledPath = (await owner.GetCustomFieldDefinitionsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .Single(candidate => candidate.Id == definition.Id)
            .JsonPaths.Should().ContainSingle().Which;
        persistedDisabledPath.Filterable.Should().BeFalse();
        persistedDisabledPath.Sortable.Should().BeFalse();

        var filteredAfterDisable = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter
            {
                Ids = [.. ids],
                CustomFieldCriteria =
                [
                    new CustomFieldCriterion
                    {
                        Key = key,
                        JsonPath = scorePath,
                        Type = CustomFieldTypes.Number,
                        Modifier = CriterionModifier.GreaterThan,
                        Value = "0",
                    },
                ],
            },
            FindFilter = new FindFilter { PerPage = 10 },
        }, TestContext.Current.CancellationToken);
        filteredAfterDisable.Items.Should().BeEmpty();

        var sortedAfterDisable = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [.. ids] },
            FindFilter = new FindFilter
            {
                // Use a distinct request shape so the endpoint's one-second result cache cannot
                // mask the capability change being asserted here.
                PerPage = 9,
                Sort = $"custom-json:number:{key}:{Uri.EscapeDataString(scorePath)}",
                Direction = Cove.Core.Enums.SortDirection.Asc,
            },
        }, TestContext.Current.CancellationToken);
        sortedAfterDisable.Items.Select(video => video.Id).Should().Equal(ids.OrderBy(id => id));

        var definitionWithoutPaths = await owner.UpdateCustomFieldDefinitionAsync(definition.Id, new CustomFieldDefinitionUpdateDto
        {
            JsonPaths = [],
        }, TestContext.Current.CancellationToken);
        definitionWithoutPaths.JsonPaths.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("POST", "/api/videos")]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenTextBooleanAndSparseJsonPaths_WhenFilteredAndSorted_ThenPointerAndNullSemanticsAreStable()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"json_scalars_{suffix}";
        const string scorePath = "/profile/score";
        const string reviewedPath = "/profile/reviewed";
        const string escapedTextPath = "/items/0/a~1b~0c ";
        const string numericObjectKeyPath = "/years/2024/value";
        const string leadingZeroPath = "/numeric/01/value";
        const string negativeIndexPath = "/numeric/-1/value";
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "JSON scalar metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto { Path = scorePath, Label = "Score", Type = CustomFieldTypes.Number, Filterable = true, Sortable = true },
                new CustomFieldJsonPathDefinitionDto { Path = reviewedPath, Label = "Reviewed", Type = CustomFieldTypes.Boolean, Filterable = true, Sortable = true },
                new CustomFieldJsonPathDefinitionDto { Path = escapedTextPath, Label = "Escaped text", Type = CustomFieldTypes.Text, Filterable = true, Sortable = true },
                new CustomFieldJsonPathDefinitionDto { Path = numericObjectKeyPath, Label = "Numeric object key", Type = CustomFieldTypes.Text, Filterable = true, Sortable = false },
                new CustomFieldJsonPathDefinitionDto { Path = leadingZeroPath, Label = "Leading-zero object key", Type = CustomFieldTypes.Text, Filterable = true, Sortable = false },
                new CustomFieldJsonPathDefinitionDto { Path = negativeIndexPath, Label = "Negative object key", Type = CustomFieldTypes.Text, Filterable = true, Sortable = false },
            ],
        }, TestContext.Current.CancellationToken);

        static JsonElement Value(int? score, bool? reviewed, object[] items, string? yearValue)
            => JsonSerializer.SerializeToElement(new
            {
                profile = new { score, reviewed },
                items,
                years = new Dictionary<string, object?> { ["2024"] = new { value = yearValue } },
            });
        static object Item(object value)
            => new Dictionary<string, object?> { ["a/b~c "] = value };

        var alpha = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar alpha {suffix}")
            .WithCustomField(key, Value(20, true, [Item(" Alpha ")], "alpha-year"))
            .Build(), TestContext.Current.CancellationToken);
        var beta = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar beta {suffix}")
            .WithCustomField(key, Value(20, false, [Item("beta")], "beta-year"))
            .Build(), TestContext.Current.CancellationToken);
        var explicitNull = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar null {suffix}")
            .WithCustomField(key, Value(null, null, [], null))
            .Build(), TestContext.Current.CancellationToken);
        var missingOrWrongType = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar missing {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new
            {
                profile = new { other = true },
                items = new[] { Item(42) },
            }))
            .Build(), TestContext.Current.CancellationToken);
        var ids = new[] { alpha.Id, beta.Id, explicitNull.Id, missingOrWrongType.Id };

        async Task<IReadOnlyList<int>> FilterAsync(string path, string type, CriterionModifier modifier, string value = "", string? value2 = null, IReadOnlyList<int>? candidateIds = null)
        {
            var response = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
            {
                ObjectFilter = new VideoFilter
                {
                    Ids = [.. (candidateIds ?? ids)],
                    CustomFieldCriteria =
                    [
                        new CustomFieldCriterion
                        {
                            Key = key,
                            JsonPath = path,
                            Type = type,
                            Modifier = modifier,
                            Value = value,
                            Value2 = value2,
                        },
                    ],
                },
                FindFilter = new FindFilter { PerPage = 10 },
            }, TestContext.Current.CancellationToken);
            return response.Items.Select(video => video.Id).ToArray();
        }

        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.Includes, "alp"))
            .Should().Equal(alpha.Id);
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.Equals, " Alpha "))
            .Should().Equal(alpha.Id);
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.Equals, "Alpha"))
            .Should().BeEmpty();
        (await FilterAsync(numericObjectKeyPath, CustomFieldTypes.Text, CriterionModifier.Equals, "alpha-year"))
            .Should().Equal(alpha.Id);
        (await FilterAsync(reviewedPath, CustomFieldTypes.Boolean, CriterionModifier.Equals, "true"))
            .Should().Equal(alpha.Id);
        (await FilterAsync(reviewedPath, CustomFieldTypes.Boolean, CriterionModifier.NotEquals, "true"))
            .Should().BeEquivalentTo([beta.Id, explicitNull.Id, missingOrWrongType.Id]);
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.IsNull))
            .Should().BeEquivalentTo([explicitNull.Id, missingOrWrongType.Id]);
        (await FilterAsync(scorePath, CustomFieldTypes.Number, CriterionModifier.NotBetween, "15", "25"))
            .Should().BeEquivalentTo([explicitNull.Id, missingOrWrongType.Id]);
        (await FilterAsync(scorePath, CustomFieldTypes.Number, CriterionModifier.GreaterThan, "1e1"))
            .Should().BeEquivalentTo([alpha.Id, beta.Id]);
        (await FilterAsync(scorePath, CustomFieldTypes.Number, CriterionModifier.GreaterThan, "not-a-number"))
            .Should().BeEmpty();

        var emptyText = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar empty text {suffix}")
            .WithCustomField(key, Value(1, true, [Item("")], "empty-year"))
            .Build(), TestContext.Current.CancellationToken);
        var whitespaceText = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar whitespace text {suffix}")
            .WithCustomField(key, Value(1, true, [Item("   ")], "whitespace-year"))
            .Build(), TestContext.Current.CancellationToken);
        var exactTextIds = new[] { emptyText.Id, whitespaceText.Id };
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.Equals, candidateIds: exactTextIds))
            .Should().Equal(emptyText.Id);
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.Equals, "   ", candidateIds: exactTextIds))
            .Should().Equal(whitespaceText.Id);
        (await FilterAsync(escapedTextPath, CustomFieldTypes.Text, CriterionModifier.NotEquals, candidateIds: exactTextIds))
            .Should().Equal(whitespaceText.Id);

        var numericObjectKeys = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar numeric object keys {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new
            {
                numeric = new Dictionary<string, object?>
                {
                    ["01"] = new { value = "leading-zero-key" },
                    ["-1"] = new { value = "negative-key" },
                },
            }))
            .Build(), TestContext.Current.CancellationToken);
        var numericArray = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"JSON scalar numeric array {suffix}")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new
            {
                numeric = new[] { new { value = "zero" }, new { value = "one" } },
            }))
            .Build(), TestContext.Current.CancellationToken);
        var numericContainerIds = new[] { numericObjectKeys.Id, numericArray.Id };
        (await FilterAsync(leadingZeroPath, CustomFieldTypes.Text, CriterionModifier.Equals, "leading-zero-key", candidateIds: numericContainerIds))
            .Should().Equal(numericObjectKeys.Id);
        (await FilterAsync(leadingZeroPath, CustomFieldTypes.Text, CriterionModifier.Equals, "one", candidateIds: numericContainerIds))
            .Should().BeEmpty();
        (await FilterAsync(negativeIndexPath, CustomFieldTypes.Text, CriterionModifier.Equals, "negative-key", candidateIds: numericContainerIds))
            .Should().Equal(numericObjectKeys.Id);
        (await FilterAsync(negativeIndexPath, CustomFieldTypes.Text, CriterionModifier.Equals, "one", candidateIds: numericContainerIds))
            .Should().BeEmpty();

        var descending = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [.. ids] },
            FindFilter = new FindFilter
            {
                PerPage = 10,
                Sort = $"custom-json:number:{key}:{Uri.EscapeDataString(scorePath)}",
                Direction = Cove.Core.Enums.SortDirection.Desc,
            },
        }, TestContext.Current.CancellationToken);
        descending.Items.Select(video => video.Id).Should().Equal(beta.Id, alpha.Id, missingOrWrongType.Id, explicitNull.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("GET", "/api/faces")]
    public async Task GivenFaceJsonValues_WhenFaceQueryUsesJsonPath_ThenItsQueryStringParserPreservesTheTypedTarget()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"face_json_{suffix}";
        const string path = "/profile/Score";
        var definition = await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "Face JSON metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Face],
            JsonPaths = [new CustomFieldJsonPathDefinitionDto { Path = path, Label = "Score", Type = CustomFieldTypes.Number, Filterable = true, Sortable = true }],
        }, TestContext.Current.CancellationToken);
        var high = await owner.CreateFaceAsync(new FaceCreateDto($"Face JSON high {suffix}", null, false, null), TestContext.Current.CancellationToken);
        var low = await owner.CreateFaceAsync(new FaceCreateDto($"Face JSON low {suffix}", null, false, null), TestContext.Current.CancellationToken);
        var missing = await owner.CreateFaceAsync(new FaceCreateDto($"Face JSON missing {suffix}", null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().SaveCustomFieldJsonValueAsync(
            definition.Id,
            CustomFieldEntityTypes.Face,
            high.Id,
            JsonSerializer.SerializeToElement(new { profile = new { Score = 30 } }),
            TestContext.Current.CancellationToken);
        await AsDbUser().SaveCustomFieldJsonValueAsync(
            definition.Id,
            CustomFieldEntityTypes.Face,
            low.Id,
            JsonSerializer.SerializeToElement(new { profile = new { Score = 10 } }),
            TestContext.Current.CancellationToken);

        var filtered = await owner.FindFacesAsync(
        [
            new CustomFieldCriterion
            {
                Key = key,
                JsonPath = path,
                Type = CustomFieldTypes.Number,
                Modifier = CriterionModifier.GreaterThan,
                Value = "15",
            },
        ], cancellationToken: TestContext.Current.CancellationToken);
        filtered.Items.Select(face => face.Id).Should().Contain(high.Id).And.NotContain(low.Id).And.NotContain(missing.Id);

        var sorted = await owner.FindFacesAsync(
            sort: $"custom-json:number:{key}:{Uri.EscapeDataString(path)}",
            direction: "asc",
            cancellationToken: TestContext.Current.CancellationToken);
        var relevantIds = sorted.Items.Select(face => face.Id).Where(id => id == low.Id || id == high.Id || id == missing.Id).ToArray();
        relevantIds.Should().Equal(low.Id, high.Id, missing.Id);
    }

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

        Func<Task> populatedTypeUpdate = () => owner.UpdateCustomFieldDefinitionAsync(
            retained.Id,
            new CustomFieldDefinitionUpdateDto { Type = "json" });
        await populatedTypeUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*Remove existing custom field values*");
        Func<Task> populatedTypeReplace = () => owner.ReplaceCustomFieldDefinitionsAsync([
            ToSync(retained, retainedKey, type: "json"),
            ToSync(omitted, omittedKey),
            ToSync(control, controlKey),
        ]);
        await populatedTypeReplace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*Remove existing custom field values*");
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

    private static CustomFieldDefinitionSyncDto ToSync(CustomFieldDefinitionDto definition, string key, string? type = null)
        => new()
        {
            Id = definition.Id,
            Key = key,
            Label = definition.Label,
            Type = type ?? definition.Type,
            EntityTypes = definition.EntityTypes,
            Options = definition.Options,
            Filterable = definition.Filterable,
            Sortable = definition.Sortable,
            IsMultiValue = definition.IsMultiValue,
            JsonPaths = definition.JsonPaths,
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

    private static void AssertJsonCustomField(VideoDto video, string key, JsonElement expected)
    {
        video.CustomFields.Should().NotBeNull();
        var actual = video.CustomFields.Should().ContainKey(key)
            .WhoseValue.Should().BeOfType<JsonElement>().Which;
        JsonElement.DeepEquals(actual, expected).Should().BeTrue();
    }
}
