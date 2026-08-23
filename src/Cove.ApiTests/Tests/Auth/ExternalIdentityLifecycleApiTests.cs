using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Plugins;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class ExternalIdentityLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string ExtensionId = "com.cove.api-test-face-provider";
    private const string ProviderId = "api-test.external";
    private const string ProviderLabel = "API Test Identity";

    [Fact]
    [CoversEndpoint("POST", "/api/auth/external/links/preview")]
    [CoversEndpoint("POST", "/api/auth/external/links/confirm")]
    [CoversEndpoint("POST", "/api/auth/external/links/cancel")]
    [CoversEndpoint("DELETE", "/api/auth/external/links/{linkid:int}")]
    [CoversEndpoint("DELETE", "/api/users/{id:int}/external-links/{linkid:int}")]
    public async Task GivenAuthenticatedUsers_WhenExternalLinksArePreparedAndManaged_ThenConfirmationCancellationAndOwnershipAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var canceledSubject = $"canceled-{suffix}";
        var ownedSubject = $"owned-{suffix}";
        var adminSubject = $"admin-{suffix}";
        var controlSubject = $"control-{suffix}";

        var canceledPreparation = await AsUser(ApiTestUsers.Eva).PrepareApiTestExternalLinkAsync(
            canceledSubject,
            $"  {ProviderLabel}  ",
            "  canceled@example.test  ");
        canceledPreparation.Failure.Should().Be(ExtensionIdentityLinkPreparationFailure.None);
        canceledPreparation.Code.Should().NotBeNullOrWhiteSpace();
        var canceledPreview = await AsUser(ApiTestUsers.Eva)
            .PreviewExternalLinkAsync(canceledPreparation.Code!);
        canceledPreview.ProviderLabel.Should().Be(ProviderLabel);
        canceledPreview.AccountLabel.Should().Be("canceled@example.test");
        (await AsUser(ApiTestUsers.Anthony)
                .TryPreviewExternalLinkStatusAsync(canceledPreparation.Code!))
            .Should().Be(HttpStatusCode.BadRequest);

        await AsUser(ApiTestUsers.Eva).CancelExternalLinkAsync(canceledPreparation.Code!);
        (await AsUser(ApiTestUsers.Eva)
                .TryPreviewExternalLinkStatusAsync(canceledPreparation.Code!))
            .Should().Be(HttpStatusCode.BadRequest);
        (await AsUser(ApiTestUsers.Eva)
                .TryCancelExternalLinkStatusAsync(canceledPreparation.Code!))
            .Should().Be(HttpStatusCode.BadRequest);
        (await AsUser(ApiTestUsers.Eva).GetOwnExternalLinksAsync()).Should().BeEmpty();

        var ownedPreparation = await AsUser(ApiTestUsers.Eva).PrepareApiTestExternalLinkAsync(
            ownedSubject,
            ProviderLabel,
            "eva@example.test");
        var ownedLink = await AsUser(ApiTestUsers.Eva)
            .ConfirmExternalLinkAsync(ownedPreparation.Code!);
        AssertLink(ownedLink, "eva@example.test");
        var freshOwned = (await AsUser(ApiTestUsers.Eva).GetOwnExternalLinksAsync())
            .Should().ContainSingle().Which;
        AssertSameLink(freshOwned, ownedLink);

        (await AsUser(ApiTestUsers.Anthony).TryRemoveOwnExternalLinkStatusAsync(ownedLink.Id))
            .Should().Be(HttpStatusCode.NotFound);
        (await AsUser(ApiTestUsers.Eva).GetOwnExternalLinksAsync())
            .Select(link => link.Id)
            .Should().Equal(ownedLink.Id);
        await AsUser(ApiTestUsers.Eva).RemoveOwnExternalLinkAsync(ownedLink.Id);
        (await AsUser(ApiTestUsers.Eva).GetOwnExternalLinksAsync()).Should().BeEmpty();
        (await AsUser(ApiTestUsers.Eva).TryRemoveOwnExternalLinkStatusAsync(ownedLink.Id))
            .Should().Be(HttpStatusCode.NotFound);

        var adminPreparation = await AsUser(ApiTestUsers.Eva).PrepareApiTestExternalLinkAsync(
            adminSubject,
            ProviderLabel,
            "admin@example.test");
        var adminLink = await AsUser(ApiTestUsers.Eva)
            .ConfirmExternalLinkAsync(adminPreparation.Code!);
        var controlPreparation = await AsUser(ApiTestUsers.Anthony).PrepareApiTestExternalLinkAsync(
            controlSubject,
            ProviderLabel,
            "control@example.test");
        var controlLink = await AsUser(ApiTestUsers.Anthony)
            .ConfirmExternalLinkAsync(controlPreparation.Code!);

        (await AsUser(ApiTestUsers.Eva)
                .TryRemoveUserExternalLinkStatusAsync(adminLink.UserId, adminLink.Id))
            .Should().Be(HttpStatusCode.Forbidden);
        (await AsUser().TryRemoveUserExternalLinkStatusAsync(controlLink.UserId, adminLink.Id))
            .Should().Be(HttpStatusCode.NotFound);
        AssertSameLink(
            (await AsUser().GetUserExternalLinksAsync(adminLink.UserId)).Should().ContainSingle().Which,
            adminLink);
        AssertSameLink(
            (await AsUser().GetUserExternalLinksAsync(controlLink.UserId)).Should().ContainSingle().Which,
            controlLink);

        await AsUser().RemoveUserExternalLinkAsync(adminLink.UserId, adminLink.Id);
        (await AsUser().GetUserExternalLinksAsync(adminLink.UserId)).Should().BeEmpty();
        (await AsUser().TryRemoveUserExternalLinkStatusAsync(adminLink.UserId, adminLink.Id))
            .Should().Be(HttpStatusCode.NotFound);
        AssertSameLink(
            (await AsUser().GetUserExternalLinksAsync(controlLink.UserId)).Should().ContainSingle().Which,
            controlLink);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/auth/external/redeem")]
    public async Task GivenConfirmedExternalIdentity_WhenProviderCompletesLogin_ThenOneTimeRedemptionIssuesTheLinkedUsersSession()
    {
        var subject = $"login-{Guid.NewGuid():N}";
        var preparation = await AsUser(ApiTestUsers.Eva).PrepareApiTestExternalLinkAsync(
            subject,
            ProviderLabel,
            "login@example.test");
        var link = await AsUser(ApiTestUsers.Eva).ConfirmExternalLinkAsync(preparation.Code!);
        link.LastUsedAt.Should().BeNull();

        var browser = await AsUser(ApiTestUsers.Eva).BeginApiTestExternalLoginAsync();
        browser.BrowserBinding.Should().NotBeNullOrWhiteSpace();
        var completion = await AsUser(ApiTestUsers.Eva).CompleteApiTestExternalLoginAsync(
            browser.BrowserBinding,
            subject,
            ProviderLabel,
            "login@example.test");
        completion.Failure.Should().Be(ExtensionLoginCompletionFailure.None);
        completion.Code.Should().NotBeNullOrWhiteSpace();
        (await AsUser(ApiTestUsers.Eva).TryRedeemExternalLoginStatusAsync(" "))
            .Should().Be(HttpStatusCode.Unauthorized);

        using var externalSession = await AsUser(ApiTestUsers.Eva)
            .RedeemExternalLoginAsync(completion.Code!);
        externalSession.Username.Should().Be(ApiTestUsers.Eva);
        externalSession.Client.AccessToken.Should().NotBe(AsUser(ApiTestUsers.Eva).AccessToken);
        var current = await externalSession.Client.GetCurrentUserAsync();
        current.GetProperty("user").GetProperty("username").GetString()
            .Should().Be(ApiTestUsers.Eva);
        var usedLink = (await externalSession.Client.GetOwnExternalLinksAsync())
            .Should().ContainSingle().Which;
        AssertSameLink(usedLink, link, expectLastUsed: true);
        (await AsUser(ApiTestUsers.Eva).TryRedeemExternalLoginStatusAsync(completion.Code!))
            .Should().Be(HttpStatusCode.Unauthorized);
        (await AsUser(ApiTestUsers.Anthony).GetOwnExternalLinksAsync()).Should().BeEmpty();
    }

    private static void AssertLink(
        ExternalIdentityLinkDto link,
        string accountLabel)
    {
        link.Id.Should().BePositive();
        link.UserId.Should().BePositive();
        link.ExtensionId.Should().Be(ExtensionId);
        link.ProviderId.Should().Be(ProviderId);
        link.ProviderLabel.Should().Be(ProviderLabel);
        link.AccountLabel.Should().Be(accountLabel);
        link.CreatedAt.Should().BeAfter(DateTime.UnixEpoch).And.BeOnOrBefore(DateTime.UtcNow);
        link.LastUsedAt.Should().BeNull();
    }

    private static void AssertSameLink(
        ExternalIdentityLinkDto actual,
        ExternalIdentityLinkDto expected,
        bool expectLastUsed = false)
    {
        actual.Id.Should().Be(expected.Id);
        actual.UserId.Should().Be(expected.UserId);
        actual.ExtensionId.Should().Be(expected.ExtensionId);
        actual.ProviderId.Should().Be(expected.ProviderId);
        actual.ProviderLabel.Should().Be(expected.ProviderLabel);
        actual.AccountLabel.Should().Be(expected.AccountLabel);
        actual.CreatedAt.Should().BeCloseTo(expected.CreatedAt, TimeSpan.FromMilliseconds(1));
        if (expectLastUsed)
        {
            actual.LastUsedAt.Should().NotBeNull();
            actual.LastUsedAt!.Value.Should().BeOnOrAfter(actual.CreatedAt);
        }
        else
        {
            actual.LastUsedAt.Should().Be(expected.LastUsedAt);
        }
    }
}
