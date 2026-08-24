using Cove.ApiTests.Infrastructure;
using Cove.Plugins;

namespace Cove.ApiTests.Tests.Extensions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ExtensionRegistryApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/extensions/registry/categories")]
    [CoversEndpoint("GET", "/api/extensions/registry/search")]
    [CoversEndpoint("GET", "/api/extensions/registry/updates")]
    [CoversEndpoint("GET", "/api/extensions/registry/{extensionid}")]
    [CoversEndpoint("GET", "/api/extensions/registry/{extensionid}/dependencies")]
    [CoversEndpoint("POST", "/api/extensions/registry/install")]
    public async Task GivenFixtureRegistry_WhenMemberDiscoversAndOwnerInstalls_ThenMetadataDependenciesAndPackagesAreExact()
    {
        var firstPage = await AsUser(ApiTestUsers.Eva).SearchExtensionRegistryAsync(query: "Registry", category: "api-test", type: "extension", sort: "name", page: 1, pageSize: 1, cancellationToken: TestContext.Current.CancellationToken);
        firstPage.TotalCount.Should().Be(2);
        firstPage.Page.Should().Be(1);
        firstPage.PageSize.Should().Be(1);
        AssertSummary(
            firstPage.Items.Should().ContainSingle().Which,
            ExtensionRegistrySimulator.DependencyId,
            "API Test Registry Dependency",
            ExtensionRegistrySimulator.DependencyVersion,
            ["api-test", "dependency", "registry"]);

        var secondPage = await AsUser(ApiTestUsers.Eva).SearchExtensionRegistryAsync(query: "Registry", category: "api-test", type: "extension", sort: "name", page: 2, pageSize: 1, cancellationToken: TestContext.Current.CancellationToken);
        secondPage.TotalCount.Should().Be(2);
        secondPage.Page.Should().Be(2);
        secondPage.PageSize.Should().Be(1);
        AssertSummary(
            secondPage.Items.Should().ContainSingle().Which,
            ExtensionRegistrySimulator.TargetId,
            "API Test Registry Target",
            ExtensionRegistrySimulator.TargetVersion,
            ["api-test", "extension", "registry"]);

        var detail = await AsUser(ApiTestUsers.Eva).GetRegistryExtensionAsync(ExtensionRegistrySimulator.TargetId, TestContext.Current.CancellationToken);
        AssertSummary(
            detail,
            ExtensionRegistrySimulator.TargetId,
            "API Test Registry Target",
            ExtensionRegistrySimulator.TargetVersion,
            ["api-test", "extension", "registry"]);
        detail.Description.Should().Be("A deterministic registry target.");
        detail.Author.Should().Be("Cove API Tests");
        detail.Kind.Should().Be("bundle");
        detail.Url.Should().Be($"https://example.invalid/{ExtensionRegistrySimulator.TargetId}");
        detail.Readme.Should().Be("# API Test Registry Target\n\nDeterministic API-test registry entry.\n");
        detail.Dependencies.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                ExtensionRegistrySimulator.DependencyId,
                ">=1.0.0"));
        detail.ExternalDependencies.Should().BeEmpty();
        detail.Settings.Should().BeEmpty();
        detail.Screenshots.Should().BeEmpty();
        var version = detail.Versions.Should().ContainSingle().Which;
        version.Version.Should().Be(ExtensionRegistrySimulator.TargetVersion);
        version.ReleasedAt.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        version.Checksum.Should().MatchRegex("^[a-f0-9]{64}$");
        version.Dependencies.Should().Equal(detail.Dependencies);

        (await AsUser(ApiTestUsers.Eva).GetExtensionRegistryCategoriesAsync(TestContext.Current.CancellationToken)).Should().Equal(
            "api-test", "dependency", "extension", "faces", "registry");

        var update = (await AsUser(ApiTestUsers.Eva).GetExtensionRegistryUpdatesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which;
        update.ExtensionId.Should().Be(ExtensionRegistrySimulator.FaceProviderId);
        update.CurrentVersion.Should().Be("1.0.0");
        update.LatestVersion.Should().Be(ExtensionRegistrySimulator.FaceProviderVersion);

        var dependency = (await AsUser(ApiTestUsers.Eva).GetRegistryExtensionDependenciesAsync(ExtensionRegistrySimulator.TargetId, TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which;
        dependency.Id.Should().Be(ExtensionRegistrySimulator.DependencyId);
        dependency.VersionConstraint.Should().Be(">=1.0.0");
        dependency.Name.Should().Be("API Test Registry Dependency");
        dependency.ResolvedVersion.Should().Be(ExtensionRegistrySimulator.DependencyVersion);
        dependency.Available.Should().BeTrue();
        dependency.Installed.Should().BeFalse();

        var missingDetail = () => AsUser(ApiTestUsers.Eva).GetRegistryExtensionAsync("missing-extension");
        await missingDetail.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        var missingDependencies = () => AsUser(ApiTestUsers.Eva)
            .GetRegistryExtensionDependenciesAsync("missing-extension");
        await missingDependencies.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        try
        {
            var forbiddenInstall = () => AsUser(ApiTestUsers.Eva).InstallRegistryExtensionAsync(
                ExtensionRegistrySimulator.TargetId,
                ExtensionRegistrySimulator.TargetVersion);
            await forbiddenInstall.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.DependencyId).Should().Be(0);
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.TargetId).Should().Be(0);

            var preview = await AsUser().PreviewRegistryExtensionInstallAsync(ExtensionRegistrySimulator.TargetId, ExtensionRegistrySimulator.TargetVersion, TestContext.Current.CancellationToken);
            preview.RequiresDependencies.Should().BeTrue();
            preview.Extension.Should().Be(new RegistryInstallExtension(
                ExtensionRegistrySimulator.TargetId,
                "API Test Registry Target",
                ExtensionRegistrySimulator.TargetVersion));
            preview.MissingDependencies.Should().ContainSingle().Which.Should().Be(dependency);
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.DependencyId).Should().Be(0);
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.TargetId).Should().Be(0);
            (await AsUser().GetExtensionsAsync(TestContext.Current.CancellationToken)).Should().NotContain(extension =>
                extension.Id == ExtensionRegistrySimulator.DependencyId
                || extension.Id == ExtensionRegistrySimulator.TargetId);

            var installed = await AsUser().InstallRegistryExtensionAsync(ExtensionRegistrySimulator.TargetId, ExtensionRegistrySimulator.TargetVersion, TestContext.Current.CancellationToken);
            installed.Message.Should().Be(
                $"Extension '{ExtensionRegistrySimulator.TargetId}' v{ExtensionRegistrySimulator.TargetVersion} installed.");
            Path.GetFileName(installed.Path).Should().Be(ExtensionRegistrySimulator.TargetId);
            File.Exists(Path.Combine(installed.Path, "extension.json")).Should().BeTrue();
            installed.InstalledDependencies.Should().Equal(ExtensionRegistrySimulator.DependencyId);
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.DependencyId).Should().Be(1);
            AsExtensionRegistry().GetPackageRequestCount(ExtensionRegistrySimulator.TargetId).Should().Be(1);

            var installedExtensions = await AsUser().GetExtensionsAsync(TestContext.Current.CancellationToken);
            var installedDependency = installedExtensions.Should()
                .ContainSingle(extension => extension.Id == ExtensionRegistrySimulator.DependencyId).Which;
            installedDependency.Name.Should().Be("API Test Registry Dependency");
            installedDependency.Version.Should().Be(ExtensionRegistrySimulator.DependencyVersion);
            installedDependency.Kind.Should().Be("bundle");
            installedDependency.Source.Should().Be("registry");
            installedDependency.Enabled.Should().BeTrue();
            installedDependency.Dependencies.Should().BeEmpty();

            var installedTarget = installedExtensions.Should()
                .ContainSingle(extension => extension.Id == ExtensionRegistrySimulator.TargetId).Which;
            installedTarget.Name.Should().Be("API Test Registry Target");
            installedTarget.Version.Should().Be(ExtensionRegistrySimulator.TargetVersion);
            installedTarget.Kind.Should().Be("bundle");
            installedTarget.Source.Should().Be("registry");
            installedTarget.Enabled.Should().BeTrue();
            installedTarget.Dependencies.Should().ContainSingle()
                .Which.Should().Be(new KeyValuePair<string, string>(
                    ExtensionRegistrySimulator.DependencyId,
                    ">=1.0.0"));
        }
        finally
        {
            await EnsureRegistryExtensionsUninstalledAsync(
                ExtensionRegistrySimulator.TargetId,
                ExtensionRegistrySimulator.DependencyId);
        }
    }

    private async Task EnsureRegistryExtensionsUninstalledAsync(params string[] extensionIds)
    {
        var errors = new List<Exception>();
        foreach (var extensionId in extensionIds)
        {
            try
            {
                await AsUser().EnsureExtensionUninstalledAsync(extensionId);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
            throw new AggregateException("One or more registry fixtures could not be cleaned up.", errors);
    }

    private static void AssertSummary(
        RegistryExtensionSummary summary,
        string id,
        string name,
        string version,
        IReadOnlyList<string> categories)
    {
        summary.Id.Should().Be(id);
        summary.Name.Should().Be(name);
        summary.Version.Should().Be(version);
        summary.Categories.Should().Equal(categories);
        summary.UpdatedAt.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }
}
