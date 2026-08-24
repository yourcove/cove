using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Users;

[Collection(ApiTestLane1Collection.Name)]
public sealed class UsersAdministrationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/users")]
    [CoversEndpoint("GET", "/api/users/{id:int}")]
    [CoversEndpoint("PUT", "/api/users/{id:int}")]
    [CoversEndpoint("GET", "/api/users/{id:int}/external-links")]
    public async Task GivenOwner_WhenUserIsCreatedUpdatedAndRead_ThenPersistedAdministrationFieldsAndExternalLinksAreReturned()
    {
        var owner = AsUser();
        var username = $"user-{Guid.NewGuid():N}";
        var created = await owner.CreateUserAsync(new CreateUserRequest(
            username,
            "Original password 123!",
            DisplayName: "Initial display name",
            Email: "initial@example.test",
            MustChangePassword: true), TestContext.Current.CancellationToken);

        created.Username.Should().Be(username);
        created.DisplayName.Should().Be("Initial display name");
        created.Email.Should().Be("initial@example.test");
        created.IsActive.Should().BeTrue();
        created.HasPassword.Should().BeTrue();
        created.MustChangePassword.Should().BeTrue();
        created.Roles.Should().BeEmpty();
        (await owner.GetUserExternalLinksAsync(created.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();

        var updated = await owner.UpdateUserAsync(created.Id, new UpdateUserRequest(
            DisplayName: "Updated display name",
            Email: "updated@example.test",
            IsActive: false,
            MustChangePassword: false), TestContext.Current.CancellationToken);
        var fresh = await owner.GetUserAsync(created.Id, TestContext.Current.CancellationToken);

        foreach (var user in new[] { updated, fresh })
        {
            user.Id.Should().Be(created.Id);
            user.Username.Should().Be(username);
            user.DisplayName.Should().Be("Updated display name");
            user.Email.Should().Be("updated@example.test");
            user.IsActive.Should().BeFalse();
            user.MustChangePassword.Should().BeFalse();
            user.HasPassword.Should().BeTrue();
        }
    }

    [Fact]
    [CoversEndpoint("POST", "/api/users/{id:int}/roles")]
    [CoversEndpoint("POST", "/api/users/{id:int}/password")]
    public async Task GivenOwner_WhenUserRolesAndPasswordAreAdministered_ThenBuiltinRolePersistsAndOnlyNewPasswordCanAuthenticate()
    {
        var owner = AsUser();
        var username = $"password-user-{Guid.NewGuid():N}";
        const string originalPassword = "Original password 123!";
        const string replacementPassword = "Replacement password 456!";
        var user = await owner.CreateUserAsync(new CreateUserRequest(username, originalPassword), TestContext.Current.CancellationToken);

        var withMemberRole = await owner.SetUserRolesAsync(user.Id, [BuiltinRoles.Member], TestContext.Current.CancellationToken);
        await owner.ChangeUserPasswordAsync(user.Id, replacementPassword, TestContext.Current.CancellationToken);

        withMemberRole.Roles.Should().Equal(BuiltinRoles.Member);
        (await owner.GetUserAsync(user.Id, TestContext.Current.CancellationToken)).Roles.Should().Equal(BuiltinRoles.Member);
        (await owner.TryLoginAsync(username, originalPassword, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await owner.TryLoginAsync(username, replacementPassword, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/users/invite")]
    [CoversEndpoint("POST", "/api/users/{id:int}/invite")]
    public async Task GivenOwner_WhenPendingAndExistingUserInvitesAreCreated_ThenTokensHaveExactRedeemUrlsAndFutureExpiry()
    {
        var owner = AsUser();
        var pendingUsername = $"invited-{Guid.NewGuid():N}";
        var existing = await owner.CreateUserAsync(new CreateUserRequest(
            $"existing-{Guid.NewGuid():N}",
            "Existing password 123!"), TestContext.Current.CancellationToken);
        var before = DateTime.UtcNow;

        var pending = await owner.CreatePendingUserInviteAsync(new CreateInviteRequest(
            Username: pendingUsername,
            DisplayName: "Pending invitee",
            Email: "pending@example.test",
            Roles: [BuiltinRoles.Member]), TestContext.Current.CancellationToken);
        var existingInvite = await owner.CreateUserInviteAsync(existing.Id, TestContext.Current.CancellationToken);

        AssertInvite(pending, before, ApiUri);
        AssertInvite(existingInvite, before, ApiUri);
        pending.Token.Should().NotBe(existingInvite.Token);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/users/{id:int}/unlock")]
    [CoversEndpoint("DELETE", "/api/users/{id:int}")]
    public async Task GivenLockedAndDisposableUsers_WhenOwnerUnlocksAndDeletesThem_ThenLoginRecoversAndMemberCannotDelete()
    {
        var owner = AsUser();
        const string lockPassword = "Lockable password 123!";
        var lockable = await owner.CreateUserAsync(new CreateUserRequest($"locked-{Guid.NewGuid():N}", lockPassword), TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 8; attempt++)
            (await owner.TryLoginAsync(lockable.Username, "wrong password", TestContext.Current.CancellationToken)).Should().BeFalse();

        (await owner.GetUserAsync(lockable.Id, TestContext.Current.CancellationToken)).IsLocked.Should().BeTrue();
        await owner.UnlockUserAsync(lockable.Id, TestContext.Current.CancellationToken);
        (await owner.GetUserAsync(lockable.Id, TestContext.Current.CancellationToken)).IsLocked.Should().BeFalse();
        (await owner.TryLoginAsync(lockable.Username, lockPassword, TestContext.Current.CancellationToken)).Should().BeTrue();

        var disposable = await owner.CreateUserAsync(new CreateUserRequest(
            $"disposable-{Guid.NewGuid():N}",
            "Disposable password 123!"), TestContext.Current.CancellationToken);
        var memberDeletion = () => AsUser(ApiTestUsers.Eva).DeleteUserAsync(disposable.Id);
        await memberDeletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetUserAsync(disposable.Id, TestContext.Current.CancellationToken)).Id.Should().Be(disposable.Id);

        await owner.DeleteUserAsync(disposable.Id, TestContext.Current.CancellationToken);
        var deletedRead = () => owner.GetUserAsync(disposable.Id);
        await deletedRead.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    private static void AssertInvite(InviteTokenDto invite, DateTime before, Uri baseAddress)
    {
        invite.Token.Should().NotBeNullOrWhiteSpace();
        invite.ExpiresAt.Should().BeAfter(before);
        invite.Url.Should().Be($"{baseAddress.Scheme}://{baseAddress.Authority}/auth/redeem-invite?token={Uri.EscapeDataString(invite.Token)}");
    }
}
