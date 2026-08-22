using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane2Collection.Name)]
public sealed class SystemFfmpegCapabilitiesApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/system/ffmpeg-capabilities")]
    public async Task GivenConfiguredMediaRuntime_WhenCapabilitiesAreRead_ThenProbeCachingAndAuthorizationAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var noReadRole = $"No system read {suffix}";
        var noReadUsername = $"ffmpeg-capabilities-none-{suffix}";
        const string noReadPassword = "FFmpeg capabilities no access 123!";
        await AsUser().CreateRoleAsync(new CreateRoleRequest(noReadRole, "API test role without system read", []));
        await AsUser().CreateUserAsync(new CreateUserRequest(noReadUsername, noReadPassword, Roles: [noReadRole]));
        using var noReadSession = await AsUser().CreateAuthSessionAsync(noReadUsername, noReadPassword);

        var forbidden = () => noReadSession.Client.GetFfmpegCapabilitiesAsync();
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var first = await AsUser().GetFfmpegCapabilitiesAsync();
        var second = await AsUser().GetFfmpegCapabilitiesAsync();

        second.Should().BeEquivalentTo(first);
        first.ProbedAtUtc.Should().BeOnOrBefore(DateTime.UtcNow).And.BeAfter(DateTime.UtcNow.AddMinutes(-5));
        first.FfmpegFound.Should().Be(!string.IsNullOrWhiteSpace(first.FfmpegPath));
        if (first.FfmpegFound)
            File.Exists(first.FfmpegPath).Should().BeTrue();
        first.Accelerators.Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
        first.Decoders.Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
        first.Accelerators.Should().OnlyContain(accelerator => !string.IsNullOrWhiteSpace(accelerator));
        first.Decoders.Should().OnlyContain(decoder => !string.IsNullOrWhiteSpace(decoder));
    }
}
