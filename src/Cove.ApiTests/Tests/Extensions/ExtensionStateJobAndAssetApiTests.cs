using System.Net;
using System.Net.Http.Json;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Extensions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ExtensionStateJobAndAssetApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string ExtensionId = "com.cove.api-test-face-provider";
    private const string JobId = "record-parameters";
    private const string JobParametersStoreKey = "api-test.job.parameters";
    private const string JobProgressStoreKey = "api-test.job.progress";

    [Fact]
    [CoversEndpoint("GET", "/api/extensions/dependencies/validate")]
    [CoversEndpoint("GET", "/api/extensions/assets/{extensionid}/{**path}")]
    public async Task GivenRuntimeExtensionAssets_WhenMemberReadsDependenciesAndAssets_ThenTypesBytesAndNoCacheHeadersAreExact()
    {
        var member = AsUser(ApiTestUsers.Eva);
        (await member.ValidateExtensionDependenciesAsync()).Should().BeEmpty();

        var assets = new Dictionary<string, (string MediaType, string Content)>
        {
            ["api-test-asset.js"] = ("application/javascript", "export const apiTestAsset = \"js\";\n"),
            ["api-test-asset.mjs"] = ("application/javascript", "export const apiTestAsset = \"mjs\";\n"),
            ["api-test-asset.css"] = ("text/css", ".api-test-asset { color: rgb(1 2 3); }\n"),
            ["api-test-asset.json"] = ("application/json", "{\"asset\":\"json\"}\n"),
            ["api-test-asset.html"] = ("text/html", "<span>API test asset</span>\n"),
            ["api-test-asset.svg"] = ("image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><path d=\"M0 0h1v1H0z\"/></svg>\n"),
            ["api-test-asset.png"] = ("image/png", "api-test-png\n"),
            ["api-test-asset.jpg"] = ("image/jpeg", "api-test-jpeg\n"),
            ["api-test-asset.woff2"] = ("font/woff2", "api-test-woff2\n"),
            ["api-test-asset.woff"] = ("font/woff", "api-test-woff\n"),
            ["api-test-asset.bin"] = ("application/octet-stream", "api-test-binary\n"),
        };

        foreach (var (path, expected) in assets)
        {
            var asset = await member.GetExtensionAssetAsync(ExtensionId, path);
            asset.Content.Should().Equal(Encoding.UTF8.GetBytes(expected.Content));
            asset.MediaType.Should().Be(expected.MediaType);
            asset.CacheControl.Should().NotBeNull();
            asset.CacheControl!.NoCache.Should().BeTrue();
            asset.CacheControl.NoStore.Should().BeTrue();
            asset.CacheControl.MustRevalidate.Should().BeTrue();
            asset.CacheControl.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Should().BeEquivalentTo(["no-cache", "no-store", "must-revalidate"]);
            asset.Pragma.Should().Be("no-cache");
            asset.Expires.Should().Be("0");
        }

        using var client = member.CreateHttpClient();
        using var missing = await client.GetAsync($"/api/extensions/assets/{ExtensionId}/missing.bin");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/extensions/{id}/data")]
    [CoversEndpoint("PUT", "/api/extensions/{id}/data/{key}")]
    [CoversEndpoint("POST", "/api/extensions/{id}/jobs/{jobid}/run")]
    public async Task GivenStatefulJobExtension_WhenOwnerWritesStateAndRunsJob_ThenPermissionsParametersProgressAndMetadataAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEmpty("the database reset must isolate extension state between API tests");

        var forbiddenStateRead = () => member.GetExtensionDataAsync(ExtensionId);
        await forbiddenStateRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var forbiddenStateWrite = () => member.SetExtensionDataAsync(ExtensionId, "owner-value", "member attempt");
        await forbiddenStateWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEmpty();

        await owner.SetExtensionDataAsync(ExtensionId, "owner-value", "first");
        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["owner-value"] = "first",
        });
        await owner.SetExtensionDataAsync(ExtensionId, "owner-value", "second");
        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["owner-value"] = "second",
        });

        var beforeForbiddenJob = await owner.GetExtensionDataAsync(ExtensionId);
        var forbiddenJob = () => member.RunExtensionJobAsync(
            ExtensionId,
            JobId,
            new Dictionary<string, string> { ["forbidden"] = "value" });
        await forbiddenJob.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(beforeForbiddenJob);

        var parameters = new Dictionary<string, string>
        {
            ["beta"] = "two words",
            ["alpha"] = "one",
        };
        var started = await owner.RunExtensionJobAsync(ExtensionId, JobId, parameters);
        started.Message.Should().Be("Job 'Record API test parameters' started");
        started.JobId.Should().NotBeNullOrWhiteSpace();

        var completed = await owner.WaitForTerminalJobAsync(started.JobId);
        completed.Status.Should().Be(JobStatus.Completed);
        completed.Type.Should().Be($"ext:{ExtensionId}:{JobId}");
        completed.Description.Should().Be("[API Test Face Provider] Record API test parameters");
        completed.Progress.Should().Be(1);
        completed.SubTask.Should().Be("API test parameters recorded");
        completed.StartedAt.Should().BeOnOrBefore(completed.CompletedAt!.Value);
        completed.Error.Should().BeNull();

        (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["owner-value"] = "second",
            [JobParametersStoreKey] = "{\"alpha\":\"one\",\"beta\":\"two words\"}",
            [JobProgressStoreKey] = "1|API test parameters recorded",
        });

        using var client = owner.CreateHttpClient();
        using var nonStateful = await client.GetAsync("/api/extensions/builtin.direct-file/data");
        nonStateful.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknownJob = await client.PostAsJsonAsync(
            $"/api/extensions/{ExtensionId}/jobs/missing/run",
            new Dictionary<string, string>());
        unknownJob.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var nonJobExtension = await client.PostAsJsonAsync(
            "/api/extensions/builtin.direct-file/jobs/missing/run",
            new Dictionary<string, string>());
        nonJobExtension.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
