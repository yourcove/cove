using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.System;

public sealed class UpdatesApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/updates/status")]
    [CoversEndpoint("POST", "/api/updates/check")]
    public async Task GivenNewerReleaseInFeed_WhenOwnerChecks_ThenStatusOffersItWithoutAnApplyButton()
    {
        var before = await AsUser().GetUpdateStatusAsync(TestContext.Current.CancellationToken);
        before.UpdateAvailable.Should().BeFalse();
        before.LastCheckedAt.Should().BeNull();

        var status = await AsUser().CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        status.UpdateAvailable.Should().BeTrue();
        status.LastError.Should().BeNull();
        status.LastCheckedAt.Should().NotBeNull();
        status.Latest!.Version.Should().Be(UpdateFeedSimulator.OfferedVersion);
        status.Latest.NotesMarkdown.Should().Be(UpdateFeedSimulator.NotesMarkdown);
        status.Latest.RequiresMigration.Should().BeFalse();
        // The test host runs `dotnet Cove.dll` without a release marker: check-only. A test run inside a
        // container hands the server the container's markers, which it correctly reports as Docker.
        if (HostIsContainer())
        {
            status.InstallKind.Should().Be("docker");
            status.CannotApplyReason.Should().Contain("container");
        }
        else
        {
            status.InstallKind.Should().Be("source");
            status.CannotApplyReason.Should().Contain("development build");
        }

        status.CanApply.Should().BeFalse();
        status.ApplyState.Should().Be("idle");
        status.ShowBanner.Should().BeTrue();
        (await AsUser().GetUpdateStatusAsync(TestContext.Current.CancellationToken)).Latest!.Version
            .Should().Be(UpdateFeedSimulator.OfferedVersion, "the result is persisted, not refetched");
    }

    [Fact]
    public async Task GivenRecentForcedCheck_WhenCheckingAgain_ThenTooManyRequests()
    {
        await AsUser().CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        (await AsUser().SendStatusAsync(HttpMethod.Post, "/api/updates/check", new { }, TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/updates/apply")]
    [CoversEndpoint("POST", "/api/updates/revert")]
    public async Task GivenCheckOnlyInstall_WhenApplyingOrReverting_ThenNothingIsAttempted()
    {
        await AsUser().CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        (await AsUser().SendStatusAsync(HttpMethod.Post, "/api/updates/apply", new { force = true }, TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.BadRequest);
        (await AsUser().SendStatusAsync(HttpMethod.Post, "/api/updates/revert", new { }, TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.Conflict);
        (await AsUser().GetUpdateStatusAsync(TestContext.Current.CancellationToken)).ApplyState.Should().Be("idle");
    }

    [Fact]
    public async Task GivenMemberAndAdmin_WhenUsingUpdates_ThenOnlyTheOwnerIsAllowedByDefault()
    {
        const string password = "Update permissions 123!";
        var admin = $"update-admin-{Guid.NewGuid():N}";
        await AsUser().CreateUserAsync(new CreateUserRequest(admin, password, Roles: [BuiltinRoles.Admin]), TestContext.Current.CancellationToken);
        using var adminSession = await AsUser().CreateAuthSessionAsync(admin, password, TestContext.Current.CancellationToken);

        foreach (var client in new[] { AsUser(ApiTestUsers.Eva), adminSession.Client })
        {
            (await client.SendStatusAsync(HttpMethod.Get, "/api/updates/status", null, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Forbidden);
            (await client.SendStatusAsync(HttpMethod.Post, "/api/updates/check", new { }, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Forbidden);
            (await client.SendStatusAsync(HttpMethod.Post, "/api/updates/apply", new { }, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Forbidden);
            (await client.SendStatusAsync(HttpMethod.Put, "/api/updates/settings", new UpdatesConfigDto(), TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Forbidden);
        }
        (await AsUser().SendStatusAsync(HttpMethod.Get, "/api/updates/status", null, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/updates/settings")]
    public async Task GivenUpdateSettings_WhenSavedThroughUpdatesAndThenThroughGeneralConfig_ThenOnlyTheUpdatesEndpointChangesThem()
    {
        var owner = AsUser();
        try
        {
            var saved = await owner.SaveUpdateSettingsAsync(
                new UpdatesConfigDto { Channel = "stable", ShowBanner = false, AutoUpdate = "safe", AutoUpdateWindow = "01:00-02:00", CheckIntervalHours = 24 },
                TestContext.Current.CancellationToken);
            saved.Settings.AutoUpdate.Should().Be("safe");
            saved.ShowBanner.Should().BeFalse();
            saved.EffectiveChannel.Should().Be("stable");

            var config = await owner.GetSystemConfigAsync(TestContext.Current.CancellationToken);
            config.Updates!.AutoUpdate.Should().Be("safe");
            await owner.SaveSystemConfigAsync(config with { Updates = new UpdatesConfigDto { AutoUpdate = "all" } }, cancellationToken: TestContext.Current.CancellationToken);

            (await owner.GetUpdateStatusAsync(TestContext.Current.CancellationToken)).Settings.AutoUpdate.Should().Be("safe");
        }
        finally
        {
            await owner.SaveUpdateSettingsAsync(new UpdatesConfigDto(), TestContext.Current.CancellationToken);
        }
    }

    // Mirrors the container markers InstallEnvironment.Detect reads. This holds because CoveApiServer
    // launches the server through the `dotnet` host, strips COVE_INSTALL_KIND, and otherwise passes on
    // this process's environment, and the server shares this process's filesystem.
    private static bool HostIsContainer()
    {
        var runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")?.Trim();
        return File.Exists("/.dockerenv")
            || File.Exists("/run/.containerenv")
            || string.Equals(runningInContainer, "true", StringComparison.OrdinalIgnoreCase)
            || runningInContainer == "1";
    }
}
