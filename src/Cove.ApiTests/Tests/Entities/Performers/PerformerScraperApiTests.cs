using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerScraperApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string ScraperId = "api-test.performer";

    [Fact]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-name")]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-fragment")]
    [CoversEndpoint("POST", "/api/performers/{id:int}/scrape-preview")]
    public async Task GivenDeterministicPerformerProvider_WhenSystemNameFragmentAndPreviewRun_ThenResultsAndPermissionsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Preview target {suffix}")
            .WithDetails("Preview must not mutate this performer")
            .Build(), TestContext.Current.CancellationToken);
        var before = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);

        var viewerUsername = $"performer-scraper-viewer-{suffix}";
        const string viewerPassword = "Performer scraper viewer 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);

        var noReadRoleName = $"No scraper permissions {suffix}";
        await AsUser().CreateRoleAsync(new CreateRoleRequest(noReadRoleName, "API test no-permission role", []), TestContext.Current.CancellationToken);
        var noReadUsername = $"performer-scraper-none-{suffix}";
        const string noReadPassword = "Performer scraper no access 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(noReadUsername, noReadPassword, Roles: [noReadRoleName]), TestContext.Current.CancellationToken);
        using var noReadSession = await AsUser().CreateAuthSessionAsync(noReadUsername, noReadPassword, TestContext.Current.CancellationToken);

        var descriptor = (await viewerSession.Client.GetScrapersAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(scraper => scraper.Id == ScraperId).Which;
        descriptor.EntityType.Should().Be("performer");
        descriptor.SupportedScrapes.Should().BeEquivalentTo(["URL", "Name", "Fragment"]);
        descriptor.Urls.Should().Equal("https://api-test.invalid/performers/*");

        var named = await viewerSession.Client.ScrapePerformerNameAsync(new ScrapeNameRequest(
            ScraperId,
            "performer",
            $"System name {suffix}"), TestContext.Current.CancellationToken);
        AssertScraped(
            named.Should().ContainSingle().Which,
            $"System name {suffix} Scraped",
            "https://api-test.invalid/performers/search-result");

        var fragmentUrl = $"https://api-test.invalid/performers/fragment-{suffix}";
        var fragment = await viewerSession.Client.ScrapePerformerFragmentAsync(new ScrapeFragmentRequest(
            ScraperId,
            "performer",
            new Dictionary<string, object>
            {
                ["name"] = $"Fragment name {suffix}",
                ["url"] = fragmentUrl,
                ["details"] = "Ignored input details",
            }), TestContext.Current.CancellationToken);
        AssertScraped(fragment, $"Fragment name {suffix}", fragmentUrl);

        var forbiddenName = () => noReadSession.Client.ScrapePerformerNameAsync(new ScrapeNameRequest(
            ScraperId,
            "performer",
            "forbidden"));
        var forbiddenFragment = () => noReadSession.Client.ScrapePerformerFragmentAsync(new ScrapeFragmentRequest(
            ScraperId,
            "performer",
            new Dictionary<string, object> { ["name"] = "forbidden" }));
        await forbiddenName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenFragment.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var previewName = $"Preview result {suffix}";
        var previewRequest = new PerformerScrapeRequestDto("name", ScraperId, null, previewName, false);
        var forbiddenPreview = () => viewerSession.Client.PreviewPerformerScrapeAsync(performer.Id, previewRequest);
        await forbiddenPreview.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(before);

        var preview = await AsUser(ApiTestUsers.Eva).PreviewPerformerScrapeAsync(performer.Id, previewRequest, TestContext.Current.CancellationToken);
        preview.InputKind.Should().Be("name");
        preview.SourceValue.Should().Be(previewName);
        AssertScraped(preview.Scraped, $"{previewName} Scraped", "https://api-test.invalid/performers/search-result");
        (await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(before);

        var missing = () => AsUser(ApiTestUsers.Eva).PreviewPerformerScrapeAsync(int.MaxValue, previewRequest);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers/{id:int}/scrape-url")]
    [CoversEndpoint("POST", "/api/performers/{id:int}/scrape")]
    public async Task GivenPerformerScrapeInputs_WhenMemberAppliesUrlAndNameResults_ThenMetadataAuthorizationAndControlsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var scrapedTag = await AsUser().CreateTagAsync("API Test Scraped Tag", TestContext.Current.CancellationToken);
        var url = $"https://api-test.invalid/performers/url-{suffix}";
        var urlTarget = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"URL target {suffix}")
            .WithDetails("Stale URL details")
            .WithAlias("Retained URL alias")
            .WithUrl($"https://local.invalid/url-{suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var nameTarget = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Name target {suffix}")
            .WithDetails("Stale name details")
            .WithAlias("Retained name alias")
            .Build(), TestContext.Current.CancellationToken);
        var control = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Scrape control {suffix}")
            .WithDetails("Control details")
            .WithAlias("Control alias")
            .Build(), TestContext.Current.CancellationToken);
        var urlBefore = await AsUser().GetPerformerByIdAsync(urlTarget.Id, TestContext.Current.CancellationToken);
        var nameBefore = await AsUser().GetPerformerByIdAsync(nameTarget.Id, TestContext.Current.CancellationToken);
        var controlBefore = await AsUser().GetPerformerByIdAsync(control.Id, TestContext.Current.CancellationToken);

        var viewerUsername = $"performer-apply-viewer-{suffix}";
        const string viewerPassword = "Performer apply viewer 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var forbiddenUrl = () => viewerSession.Client.ScrapePerformerUrlAsync(
            urlTarget.Id,
            new PerformerScrapeUrlRequestDto(url, false));
        var forbiddenName = () => viewerSession.Client.ScrapePerformerAsync(
            nameTarget.Id,
            new PerformerScrapeRequestDto("name", ScraperId, null, $"Named result {suffix}", false));
        await forbiddenUrl.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetPerformerByIdAsync(urlTarget.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(urlBefore);
        (await AsUser().GetPerformerByIdAsync(nameTarget.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(nameBefore);

        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var denyWrite = await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Performer,
            nameTarget.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var entityForbidden = () => AsUser(ApiTestUsers.Eva).ScrapePerformerAsync(
            nameTarget.Id,
            new PerformerScrapeRequestDto("name", ScraperId, null, $"Named result {suffix}", false));
        await entityForbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetPerformerByIdAsync(nameTarget.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(nameBefore);
        await AsUser().DeleteEntityOverrideAsync(denyWrite.Id, TestContext.Current.CancellationToken);

        var urlApplied = await AsUser(ApiTestUsers.Eva).ScrapePerformerUrlAsync(urlTarget.Id, new PerformerScrapeUrlRequestDto(url, false), TestContext.Current.CancellationToken);
        AssertApplied(urlApplied, "API Test URL Performer", url, scrapedTag.Id, "Retained URL alias");
        AssertApplied(
            await AsUser().GetPerformerByIdAsync(urlTarget.Id, TestContext.Current.CancellationToken),
            "API Test URL Performer",
            url,
            scrapedTag.Id,
            "Retained URL alias");

        var name = $"Named result {suffix}";
        var nameApplied = await AsUser(ApiTestUsers.Eva).ScrapePerformerAsync(nameTarget.Id, new PerformerScrapeRequestDto("name", ScraperId, null, name, false), TestContext.Current.CancellationToken);
        AssertApplied(
            nameApplied,
            $"{name} Scraped",
            "https://api-test.invalid/performers/search-result",
            scrapedTag.Id,
            "Retained name alias");
        AssertApplied(
            await AsUser().GetPerformerByIdAsync(nameTarget.Id, TestContext.Current.CancellationToken),
            $"{name} Scraped",
            "https://api-test.invalid/performers/search-result",
            scrapedTag.Id,
            "Retained name alias");
        (await AsUser().GetPerformerByIdAsync(control.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(controlBefore);

        var missing = () => AsUser(ApiTestUsers.Eva).ScrapePerformerUrlAsync(
            int.MaxValue,
            new PerformerScrapeUrlRequestDto(url, false));
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    private static void AssertScraped(ScrapedPerformerDto actual, string name, string expectedUrl)
    {
        actual.SourceScraperId.Should().Be(ScraperId);
        actual.Name.Should().Be(name);
        actual.Disambiguation.Should().Be("API test provider");
        actual.Gender.Should().Be("Female");
        actual.Birthdate.Should().Be("1990-02-03");
        actual.Country.Should().Be("Canada");
        actual.Ethnicity.Should().Be("API test ethnicity");
        actual.EyeColor.Should().Be("Green");
        actual.HairColor.Should().Be("Brown");
        actual.HeightCm.Should().Be(172);
        actual.Weight.Should().Be(63);
        actual.Measurements.Should().Be("34-25-35");
        actual.Tattoos.Should().Be("API test tattoo");
        actual.Piercings.Should().Be("API test piercing");
        actual.Details.Should().Be("Deterministic API test performer details");
        actual.Urls.Should().Equal(expectedUrl);
        actual.Aliases.Should().Equal("API Test Scraped Alias");
        actual.TagNames.Should().Equal("API Test Scraped Tag");
        actual.ImageUrl.Should().BeNull();
    }

    private static void AssertApplied(
        PerformerDto actual,
        string name,
        string expectedUrl,
        int expectedTagId,
        string retainedAlias)
    {
        actual.Name.Should().Be(name);
        actual.Disambiguation.Should().Be("API test provider");
        actual.Gender.Should().Be("Female");
        actual.Birthdate.Should().Be("1990-02-03");
        actual.Country.Should().Be("CA");
        actual.Ethnicity.Should().Be("API test ethnicity");
        actual.EyeColor.Should().Be("Green");
        actual.HairColor.Should().Be("Brown");
        actual.HeightCm.Should().Be(172);
        actual.Weight.Should().Be(63);
        actual.Measurements.Should().Be("34-25-35");
        actual.Tattoos.Should().Be("API test tattoo");
        actual.Piercings.Should().Be("API test piercing");
        actual.Details.Should().Be("Deterministic API test performer details");
        actual.Urls.Should().Contain(expectedUrl);
        actual.Aliases.Should().BeEquivalentTo([retainedAlias, "API Test Scraped Alias"]);
        actual.Tags.Should().ContainSingle(tag => tag.Id == expectedTagId);
        actual.ImagePath.Should().BeNull();
    }
}
