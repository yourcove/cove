using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions.Execution;
using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ScrapeApplicationLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/scrape-attempts/{id:guid}")]
    [CoversEndpoint("POST", "/api/scrape-attempts")]
    [CoversEndpoint("POST", "/api/scrape-attempts/{id:guid}/apply")]
    public async Task GivenLoopbackTextMetadata_WhenScrapeAttemptIsCreatedReadAndApplied_ThenPermissionsAndPersistenceAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var originalTag = await AsUser().CreateTagAsync($"Original scrape tag {suffix}");
        var target = await AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Original scrape text {suffix}")
            .WithDetails($"Original scrape details {suffix}")
            .WithUrl($"https://text.example/original/{suffix}")
            .WithTag(originalTag)
            .Build());
        var control = await AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Control scrape text {suffix}")
            .WithDetails($"Control scrape details {suffix}")
            .Build());
        var beforeTarget = await AsUser().GetTextByIdAsync(target.Id);
        var beforeControl = await AsUser().GetTextByIdAsync(control.Id);
        var scrapedTitle = $"Applied scrape text {suffix}";
        var scrapedTag = $"Applied scrape tag {suffix}";
        var scrapedDetails = $"Applied scrape details {suffix}";
        var source = AsDownloadSource().CreateFile(
            $"scrape-{suffix}.html",
            "text/html",
            Encoding.UTF8.GetBytes(
                $"<html><head><title>{scrapedTitle} [{scrapedTag}]</title><meta name=\"description\" content=\"{scrapedDetails}\"></head><body></body></html>"));
        var createRequest = new CreateScrapeAttemptDto(
            "builtin.generic:text",
            EntityKinds.Text,
            target.Id,
            "url",
            source.Uri.AbsoluteUri,
            Name: null,
            Fragment: null);

        var created = await AsUser(ApiTestUsers.Eva).CreateScrapeAttemptAsync(createRequest);
        var fetched = await AsUser(ApiTestUsers.Eva).GetScrapeAttemptAsync(created.Id);

        var viewerUsername = $"scrape-viewer-{suffix}";
        const string viewerPassword = "Scrape application viewer 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var forbiddenCreate = await SendForStatusAsync(
            viewerSession.Client,
            HttpMethod.Post,
            "/api/scrape-attempts",
            createRequest);
        var forbiddenGet = await SendForStatusAsync(
            viewerSession.Client,
            HttpMethod.Get,
            $"/api/scrape-attempts/{created.Id:D}");
        var forbiddenApply = await SendForStatusAsync(
            viewerSession.Client,
            HttpMethod.Post,
            $"/api/scrape-attempts/{created.Id:D}/apply",
            BuildTextApplyRequest());

        var invalidCreate = await SendForStatusAsync(
            AsUser(ApiTestUsers.Eva),
            HttpMethod.Post,
            "/api/scrape-attempts",
            createRequest with { EntityType = "unsupported" });
        var missingAttemptId = Guid.NewGuid();
        var missingGet = await SendForStatusAsync(
            AsUser(ApiTestUsers.Eva),
            HttpMethod.Get,
            $"/api/scrape-attempts/{missingAttemptId:D}");
        var missingApply = await SendForStatusAsync(
            AsUser(ApiTestUsers.Eva),
            HttpMethod.Post,
            $"/api/scrape-attempts/{missingAttemptId:D}/apply",
            BuildTextApplyRequest());

        var afterDenied = await AsUser().GetTextByIdAsync(target.Id);
        var applied = await AsUser(ApiTestUsers.Eva).ApplyScrapeAttemptAsync(created.Id, BuildTextApplyRequest());
        var persistedAttempt = await AsUser(ApiTestUsers.Eva).GetScrapeAttemptAsync(created.Id);
        var persistedTarget = await AsUser().GetTextByIdAsync(target.Id);
        var persistedControl = await AsUser().GetTextByIdAsync(control.Id);

        using var assertions = new AssertionScope();
        source.RequestCount.Should().Be(1);
        created.Id.Should().NotBeEmpty();
        created.ScraperId.Should().Be("builtin.generic:text");
        created.EntityType.Should().Be(EntityKinds.Text);
        created.EntityId.Should().Be(target.Id);
        created.InputKind.Should().Be("url");
        created.Status.Should().Be(ScrapeAttemptStatuses.Success);
        created.Error.Should().BeNull();
        created.AppliedAt.Should().BeNull();
        AssertAttemptPayload(created, beforeTarget, source.Uri, scrapedTitle, scrapedDetails, scrapedTag);
        fetched.Should().BeEquivalentTo(created, options => options
            .Excluding(attempt => attempt.CreatedAt)
            .Excluding(attempt => attempt.EntitySnapshotJson));
        AssertJsonEquivalent(fetched.EntitySnapshotJson, created.EntitySnapshotJson);
        DateTime.Parse(fetched.CreatedAt).Should().BeCloseTo(DateTime.Parse(created.CreatedAt), TimeSpan.FromMilliseconds(1));
        forbiddenCreate.Should().Be(HttpStatusCode.Forbidden);
        forbiddenGet.Should().Be(HttpStatusCode.Forbidden);
        forbiddenApply.Should().Be(HttpStatusCode.Forbidden);
        invalidCreate.Should().Be(HttpStatusCode.BadRequest);
        missingGet.Should().Be(HttpStatusCode.NotFound);
        missingApply.Should().Be(HttpStatusCode.NotFound);
        AssertTextUnchanged(afterDenied, beforeTarget);
        applied.Id.Should().Be(created.Id);
        applied.Status.Should().Be(ScrapeAttemptStatuses.Applied);
        applied.AppliedAt.Should().NotBeNull();
        persistedAttempt.Status.Should().Be(ScrapeAttemptStatuses.Applied);
        persistedAttempt.AppliedAt.Should().NotBeNull();
        persistedTarget.Title.Should().Be(scrapedTitle);
        persistedTarget.Details.Should().Be(scrapedDetails);
        persistedTarget.Organized.Should().BeTrue();
        persistedTarget.Urls.Should().Equal(source.Uri.AbsoluteUri);
        persistedTarget.Tags.Select(tag => tag.Name).Should().Equal(scrapedTag);
        persistedTarget.Tags.Should().NotContain(tag => tag.Id == originalTag.Id);
        AssertTextUnchanged(persistedControl, beforeControl);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/scrape-attempts/resolve-relations")]
    [CoversEndpoint("POST", "/api/performers/{id:int}/apply-scraped")]
    public async Task GivenExistingRelationsAndScrapedPerformer_WhenResolvedAndApplied_ThenSelectionsPermissionsAndControlsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var resolvedTag = await AsUser().CreateTagAsync(new TagBuilder()
            .WithName($"Resolved scrape tag {suffix}")
            .WithAlias($"Resolved tag alias {suffix}")
            .Build());
        var staleTag = await AsUser().CreateTagAsync($"Stale scrape tag {suffix}");
        var resolvedPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Resolved scrape performer {suffix}")
            .WithAlias($"Nonidentity performer alias {suffix}")
            .Build());
        var target = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original apply performer {suffix}")
            .WithDetails($"Preserved performer details {suffix}")
            .WithCountry("Original country")
            .WithAlias($"Stale performer alias {suffix}")
            .WithUrl($"https://performer.example/original/{suffix}")
            .WithTag(staleTag)
            .Build());
        var control = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Control apply performer {suffix}")
            .WithDetails($"Control performer details {suffix}")
            .Build());
        var beforeTarget = await AsUser().GetPerformerByIdAsync(target.Id);
        var beforeControl = await AsUser().GetPerformerByIdAsync(control.Id);
        var missingPerformerName = $"Missing scrape performer {suffix}";
        var missingTagName = $"Missing scrape tag {suffix}";
        var relationRequest = new ResolveScrapeRelationsRequestDto
        {
            Performers =
            [
                $" {resolvedPerformer.Name} ",
                resolvedPerformer.Aliases.Single(),
                missingPerformerName,
            ],
            Tags =
            [
                resolvedTag.Name,
                $" {resolvedTag.Aliases.Single()} ",
                missingTagName,
            ],
        };
        var applyRequest = new PerformerApplyScrapedRequestDto
        {
            Scraped = new ScrapedPerformerDto
            {
                SourceScraperId = "api-test-performer",
                Name = $" Applied performer {suffix} ",
                Disambiguation = " Applied disambiguation ",
                Gender = "female",
                Birthdate = "1990-02-03",
                Country = " Applied country ",
                Ethnicity = " Applied ethnicity ",
                EyeColor = " Applied eyes ",
                HairColor = " Applied hair ",
                HeightCm = 171,
                Weight = 63,
                Measurements = " 34-25-35 ",
                Tattoos = " Applied tattoo ",
                Piercings = " Applied piercing ",
                Details = "This field is intentionally excluded.",
                ImageUrl = null,
                Urls = [$" https://performer.example/applied/{suffix} ", $"https://performer.example/applied/{suffix}"],
                Aliases = [$" Applied alias {suffix} ", $"Applied alias {suffix}"],
                TagNames = [resolvedTag.Aliases.Single(), missingTagName],
            },
            CreateMissingTags = false,
            ReplaceFields =
            [
                "name",
                "disambiguation",
                "gender",
                "birthdate",
                "country",
                "ethnicity",
                "eyeColor",
                "hairColor",
                "heightCm",
                "weight",
                "measurements",
                "tattoos",
                "piercings",
            ],
            CollectionModes = new Dictionary<string, string>
            {
                ["urls"] = "replace",
                ["aliases"] = "replace",
                ["tags"] = "replace",
            },
        };

        var resolved = await AsUser(ApiTestUsers.Eva).ResolveScrapeRelationsAsync(relationRequest);
        var performersAfterResolve = await AsUser().GetPerformersAsync();
        var tagsAfterResolve = await AsUser().GetTagsAsync();

        var viewerUsername = $"performer-apply-viewer-{suffix}";
        const string viewerPassword = "Performer apply viewer 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var forbiddenResolve = await SendForStatusAsync(
            viewerSession.Client,
            HttpMethod.Post,
            "/api/scrape-attempts/resolve-relations",
            relationRequest);
        var forbiddenApply = await SendForStatusAsync(
            viewerSession.Client,
            HttpMethod.Post,
            $"/api/performers/{target.Id}/apply-scraped",
            applyRequest);
        var missingApply = await SendForStatusAsync(
            AsUser(),
            HttpMethod.Post,
            $"/api/performers/{int.MaxValue}/apply-scraped",
            applyRequest);

        var afterDenied = await AsUser().GetPerformerByIdAsync(target.Id);
        var applied = await AsUser(ApiTestUsers.Eva).ApplyScrapedPerformerAsync(target.Id, applyRequest);
        var persisted = await AsUser().GetPerformerByIdAsync(target.Id);
        var persistedControl = await AsUser().GetPerformerByIdAsync(control.Id);

        using var assertions = new AssertionScope();
        resolved.Performers.Should().ContainSingle();
        resolved.Performers[0].Input.Should().Be(resolvedPerformer.Name);
        resolved.Performers[0].MatchedName.Should().Be(resolvedPerformer.Name);
        resolved.Tags.Should().BeEquivalentTo(new[]
        {
            new ScrapeRelationMatchDto(resolvedTag.Name, resolvedTag.Name),
            new ScrapeRelationMatchDto(resolvedTag.Aliases.Single(), resolvedTag.Name),
        });
        resolved.Performers.Should().NotContain(match => match.Input == resolvedPerformer.Aliases.Single());
        resolved.Performers.Should().NotContain(match => match.Input == missingPerformerName);
        resolved.Tags.Should().NotContain(match => match.Input == missingTagName);
        performersAfterResolve.Should().NotContain(performer => performer.Name == missingPerformerName);
        tagsAfterResolve.Should().NotContain(tag => tag.Name == missingTagName);
        forbiddenResolve.Should().Be(HttpStatusCode.Forbidden);
        forbiddenApply.Should().Be(HttpStatusCode.Forbidden);
        missingApply.Should().Be(HttpStatusCode.NotFound);
        AssertPerformerUnchanged(afterDenied, beforeTarget);
        AssertAppliedPerformer(applied, applyRequest, resolvedTag, beforeTarget.Details);
        AssertAppliedPerformer(persisted, applyRequest, resolvedTag, beforeTarget.Details);
        persisted.Tags.Should().NotContain(tag => tag.Id == staleTag.Id);
        AssertPerformerUnchanged(persistedControl, beforeControl);
    }

    private static ApplyVideoScrapeAttemptDto BuildTextApplyRequest() => new(
        ReplaceFields: ["title", "details"],
        CollectionModes: new Dictionary<string, string>
        {
            ["urls"] = "replace",
            ["tags"] = "replace",
        },
        CreateMissingTags: true,
        CreateMissingPerformers: false,
        CreateMissingStudio: false,
        MarkOrganized: true);

    private static async Task<HttpStatusCode> SendForStatusAsync(
        CoveClient user,
        HttpMethod method,
        string requestUri,
        object? payload = null)
    {
        using var client = user.CreateHttpClient();
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static void AssertAttemptPayload(
        ScrapeAttemptDto attempt,
        TextDocumentDto beforeTarget,
        Uri sourceUri,
        string scrapedTitle,
        string scrapedDetails,
        string scrapedTag)
    {
        using var input = JsonDocument.Parse(attempt.InputJson!);
        input.RootElement.GetProperty("url").GetString().Should().Be(sourceUri.AbsoluteUri);
        using var result = JsonDocument.Parse(attempt.ResultJson!);
        result.RootElement.GetProperty("title").GetString().Should().Be(scrapedTitle);
        result.RootElement.GetProperty("details").GetString().Should().Be(scrapedDetails);
        result.RootElement.GetProperty("tags").EnumerateArray().Select(item => item.GetString()).Should().Equal(scrapedTag);
        result.RootElement.GetProperty("urls").EnumerateArray().Select(item => item.GetString()).Should().Equal(sourceUri.AbsoluteUri);
        using var snapshot = JsonDocument.Parse(attempt.EntitySnapshotJson!);
        snapshot.RootElement.GetProperty("title").GetString().Should().Be(beforeTarget.Title);
        snapshot.RootElement.GetProperty("details").GetString().Should().Be(beforeTarget.Details);
        snapshot.RootElement.GetProperty("urls").EnumerateArray().Select(item => item.GetString()).Should().Equal(beforeTarget.Urls);
        snapshot.RootElement.GetProperty("tags").EnumerateArray().Select(item => item.GetString()).Should().Equal(beforeTarget.Tags.Select(tag => tag.Name));
    }

    private static void AssertJsonEquivalent(string? actual, string? expected)
    {
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected!)).Should().BeTrue();
    }

    private static void AssertTextUnchanged(TextDocumentDto actual, TextDocumentDto expected)
    {
        actual.Id.Should().Be(expected.Id);
        actual.Title.Should().Be(expected.Title);
        actual.Details.Should().Be(expected.Details);
        actual.Organized.Should().Be(expected.Organized);
        actual.Urls.Should().Equal(expected.Urls);
        actual.Tags.Select(tag => tag.Id).Should().Equal(expected.Tags.Select(tag => tag.Id));
    }

    private static void AssertAppliedPerformer(
        PerformerDto actual,
        PerformerApplyScrapedRequestDto request,
        TagDetailDto resolvedTag,
        string? preservedDetails)
    {
        var scraped = request.Scraped;
        actual.Name.Should().Be(scraped.Name!.Trim());
        actual.Disambiguation.Should().Be(scraped.Disambiguation!.Trim());
        actual.Gender.Should().Be("Female");
        actual.Birthdate.Should().Be("1990-02-03");
        actual.Country.Should().Be(scraped.Country!.Trim());
        actual.Ethnicity.Should().Be(scraped.Ethnicity!.Trim());
        actual.EyeColor.Should().Be(scraped.EyeColor!.Trim());
        actual.HairColor.Should().Be(scraped.HairColor!.Trim());
        actual.HeightCm.Should().Be(171);
        actual.Weight.Should().Be(63);
        actual.Measurements.Should().Be(scraped.Measurements!.Trim());
        actual.Tattoos.Should().Be(scraped.Tattoos!.Trim());
        actual.Piercings.Should().Be(scraped.Piercings!.Trim());
        actual.Details.Should().Be(preservedDetails);
        actual.Urls.Should().Equal(scraped.Urls[0].Trim());
        actual.Aliases.Should().Equal(scraped.Aliases[0].Trim());
        actual.Tags.Select(tag => tag.Id).Should().Equal(resolvedTag.Id);
        actual.ImagePath.Should().BeNull();
    }

    private static void AssertPerformerUnchanged(PerformerDto actual, PerformerDto expected)
    {
        actual.Id.Should().Be(expected.Id);
        actual.Name.Should().Be(expected.Name);
        actual.Details.Should().Be(expected.Details);
        actual.Country.Should().Be(expected.Country);
        actual.Urls.Should().Equal(expected.Urls);
        actual.Aliases.Should().Equal(expected.Aliases);
        actual.Tags.Select(tag => tag.Id).Should().Equal(expected.Tags.Select(tag => tag.Id));
    }
}
