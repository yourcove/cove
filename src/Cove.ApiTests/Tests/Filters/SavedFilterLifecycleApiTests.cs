using System.Text.Json.Nodes;
using Cove.Api.Controllers;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Filters;

[Collection(ApiTestLane1Collection.Name)]
public sealed class SavedFilterLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/savedfilters")]
    [CoversEndpoint("GET", "/api/savedfilters/{id:int}")]
    [CoversEndpoint("PUT", "/api/savedfilters/{id:int}")]
    public async Task GivenRichBuiltInAndExtensionFilters_WhenCreatedReadAndPartiallyUpdated_ThenNormalizationAndJsonPersistenceAreExact()
    {
        var member = AsUser(ApiTestUsers.Eva);
        const string findFilter = "{\"q\":\"night sky\",\"page\":3,\"perPage\":40,\"sort\":\"random\",\"direction\":\"Desc\",\"seed\":91234,\"sorts\":[{\"key\":\"rating\",\"direction\":\"Desc\"}]}";
        const string objectFilter = "{\"and\":[{\"organized\":{\"value\":true}},{\"tags\":{\"ids\":[11,22],\"modifier\":\"IncludesAll\"}}],\"includeSubGroups\":true}";
        const string uiOptions = "{\"view\":\"grid\",\"columns\":5,\"expanded\":[\"details\",\"files\"],\"density\":\"compact\"}";
        const string extensionModeInput = " EXT:Com.Example.Tools:Missing-Videos ";
        const string extensionMode = "ext:com.example.tools:missing-videos";

        var created = await member.CreateSavedFilterAsync(new SavedFilterCreateDto(
            " VIDEOS ",
            "  Night sky filter  ",
            findFilter,
            objectFilter,
            uiOptions));
        var extensionFilter = await member.CreateSavedFilterAsync(new SavedFilterCreateDto(
            extensionModeInput,
            "  Extension view  ",
            "{\"sort\":\"title\",\"seed\":77}",
            "{\"extensionCriterion\":{\"enabled\":true}}",
            "{\"panel\":\"extension\"}"));

        created.Mode.Should().Be("videos");
        created.Name.Should().Be("Night sky filter");
        AssertRandomSeedStripped(created.FindFilter, findFilter);
        JsonNode.DeepEquals(JsonNode.Parse(created.ObjectFilter!), JsonNode.Parse(objectFilter)).Should().BeTrue();
        JsonNode.DeepEquals(JsonNode.Parse(created.UIOptions!), JsonNode.Parse(uiOptions)).Should().BeTrue();
        extensionFilter.Mode.Should().Be(extensionMode);
        extensionFilter.Name.Should().Be("Extension view");
        JsonNode.Parse(extensionFilter.FindFilter!)!["seed"]!.GetValue<int>().Should().Be(77, "non-random sort seeds are preserved");

        var fresh = await member.GetSavedFilterAsync(created.Id);
        AssertFilterEquivalent(created, fresh);
        (await member.GetSavedFiltersAsync()).Select(filter => filter.Id).Should().Equal(extensionFilter.Id, created.Id);
        AssertFilterEquivalent(created, (await member.GetSavedFiltersAsync(" videos ")).Should().ContainSingle(filter => filter.Id == created.Id).Which);
        AssertFilterEquivalent(extensionFilter, (await member.GetSavedFiltersAsync(extensionModeInput)).Should().ContainSingle(filter => filter.Id == extensionFilter.Id).Which);

        const string replacementObjectFilter = "{\"or\":[{\"rating\":{\"min\":80}},{\"favorite\":true}]}";
        var updated = await member.UpdateSavedFilterAsync(created.Id, new SavedFilterUpdateDto(
            Mode: " IMAGES ",
            Name: "  Updated night sky  ",
            FindFilter: null,
            ObjectFilter: replacementObjectFilter,
            UIOptions: null));

        updated.Id.Should().Be(created.Id);
        updated.Mode.Should().Be("images");
        updated.Name.Should().Be("Updated night sky");
        JsonNode.DeepEquals(JsonNode.Parse(updated.FindFilter!), JsonNode.Parse(created.FindFilter!)).Should().BeTrue("a null partial-update field preserves persisted JSON");
        JsonNode.DeepEquals(JsonNode.Parse(updated.ObjectFilter!), JsonNode.Parse(replacementObjectFilter)).Should().BeTrue();
        JsonNode.DeepEquals(JsonNode.Parse(updated.UIOptions!), JsonNode.Parse(uiOptions)).Should().BeTrue();
        AssertFilterEquivalent(updated, await member.GetSavedFilterAsync(created.Id));
        (await member.GetSavedFiltersAsync("videos")).Should().NotContain(filter => filter.Id == created.Id);
        AssertFilterEquivalent(updated, (await member.GetSavedFiltersAsync("images")).Should().ContainSingle(filter => filter.Id == created.Id).Which);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/savedfilters/{id:int}")]
    public async Task GivenPerUserFiltersAndViewerDowngrade_WhenConflictsIsolationPermissionsAndDeleteRun_ThenExactOwnerStatePersists()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var suffix = Guid.NewGuid().ToString("N");
        var sharedName = $"Shared filter {suffix}";
        var evaFilter = await eva.CreateSavedFilterAsync(new SavedFilterCreateDto("videos", $"  {sharedName}  ", "{\"sort\":\"title\"}", "{\"eva\":true}", "{\"view\":\"list\"}"));

        var duplicate = () => eva.CreateSavedFilterAsync(new SavedFilterCreateDto(" VIDEOS ", $" {sharedName.ToUpperInvariant()} ", null, null, null));
        await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 409 (Conflict)*");
        AssertFilterEquivalent(evaFilter, (await eva.GetSavedFiltersAsync("videos")).Should().ContainSingle().Which);

        var anthonyFilter = await anthony.CreateSavedFilterAsync(new SavedFilterCreateDto("videos", $" {sharedName.ToUpperInvariant()} ", "{\"sort\":\"date\"}", "{\"anthony\":true}", "{\"view\":\"grid\"}"));
        anthonyFilter.Name.Should().Be(sharedName.ToUpperInvariant());
        AssertFilterEquivalent(anthonyFilter, (await anthony.GetSavedFiltersAsync("videos")).Should().ContainSingle().Which);
        await AssertNotFoundAsync(() => anthony.GetSavedFilterAsync(evaFilter.Id));
        await AssertNotFoundAsync(() => eva.GetSavedFilterAsync(anthonyFilter.Id));
        await AssertNotFoundAsync(() => anthony.UpdateSavedFilterAsync(evaFilter.Id, new SavedFilterUpdateDto(null, "Hijacked", null, null, null)));
        await AssertNotFoundAsync(() => anthony.DeleteSavedFilterAsync(evaFilter.Id));
        AssertFilterEquivalent(evaFilter, await eva.GetSavedFilterAsync(evaFilter.Id));

        await eva.DeleteSavedFilterAsync(evaFilter.Id);
        await AssertNotFoundAsync(() => eva.GetSavedFilterAsync(evaFilter.Id));
        AssertFilterEquivalent(anthonyFilter, await anthony.GetSavedFilterAsync(anthonyFilter.Id));

        var invalidMode = () => eva.CreateSavedFilterAsync(new SavedFilterCreateDto("unknown-mode", "Invalid mode", null, null, null));
        var invalidName = () => eva.CreateSavedFilterAsync(new SavedFilterCreateDto("videos", "   ", null, null, null));
        var invalidListMode = () => eva.GetSavedFiltersAsync("unknown-mode");
        await invalidMode.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await invalidName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await invalidListMode.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");

        var viewerUsername = $"filter-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Saved filter viewer 123!";
        var viewerUser = await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Member]));
        SavedFilterDto seeded;
        using (var memberSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword))
        {
            seeded = await memberSession.Client.CreateSavedFilterAsync(new SavedFilterCreateDto(
                "audios",
                "Viewer-owned filter",
                "{\"sort\":\"title\"}",
                "{\"organized\":true}",
                "{\"view\":\"table\"}"));
        }
        _ = await owner.SetUserRolesAsync(viewerUser.Id, [BuiltinRoles.Viewer]);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var viewer = viewerSession.Client;
        AssertFilterEquivalent(seeded, await viewer.GetSavedFilterAsync(seeded.Id));

        var forbiddenCreate = () => viewer.CreateSavedFilterAsync(new SavedFilterCreateDto("audios", "Forbidden create", null, null, null));
        await forbiddenCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertFilterEquivalent(seeded, await viewer.GetSavedFilterAsync(seeded.Id));
        AssertFilterEquivalent(seeded, (await viewer.GetSavedFiltersAsync("audios")).Should().ContainSingle().Which);
        var forbiddenUpdate = () => viewer.UpdateSavedFilterAsync(seeded.Id, new SavedFilterUpdateDto("images", "Forbidden update", "{}", "{}", "{}"));
        await forbiddenUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertFilterEquivalent(seeded, await viewer.GetSavedFilterAsync(seeded.Id));
        AssertFilterEquivalent(seeded, (await viewer.GetSavedFiltersAsync("audios")).Should().ContainSingle().Which);
        var forbiddenDelete = () => viewer.DeleteSavedFilterAsync(seeded.Id);
        await forbiddenDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertFilterEquivalent(seeded, await viewer.GetSavedFilterAsync(seeded.Id));
        AssertFilterEquivalent(seeded, (await viewer.GetSavedFiltersAsync("audios")).Should().ContainSingle().Which);
    }

    private static void AssertRandomSeedStripped(string? persisted, string input)
    {
        var actual = JsonNode.Parse(persisted!)!.AsObject();
        var expected = JsonNode.Parse(input)!.AsObject();
        actual.ContainsKey("seed").Should().BeFalse();
        expected.Remove("seed");
        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    private static void AssertFilterEquivalent(SavedFilterDto expected, SavedFilterDto actual)
    {
        actual.Id.Should().Be(expected.Id);
        actual.Mode.Should().Be(expected.Mode);
        actual.Name.Should().Be(expected.Name);
        AssertJsonEquivalent(expected.FindFilter, actual.FindFilter);
        AssertJsonEquivalent(expected.ObjectFilter, actual.ObjectFilter);
        AssertJsonEquivalent(expected.UIOptions, actual.UIOptions);
    }

    private static void AssertJsonEquivalent(string? expected, string? actual)
    {
        if (expected is null || actual is null)
        {
            actual.Should().Be(expected);
            return;
        }

        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).Should().BeTrue();
    }

    private static async Task AssertNotFoundAsync(Func<Task> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

    private static async Task AssertNotFoundAsync<T>(Func<Task<T>> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
}
