using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Galleries;

public sealed class GalleryRescanApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/galleries/{id:int}/rescan")]
    public async Task GivenFileBackedGallery_WhenMemberRescansIt_ThenArchiveImagesAreImportedAndPermissionFailuresHaveNoEffect()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Gallery rescan {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var archivePath = AsTestFileSystem().CreateGalleryArchive(
            $"gallery-rescan-{suffix}.zip",
            "rescanned-image.png",
            ApiTestImages.OnePixelPng());
        File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(-1));
        await AsDbUser().AttachGalleryArchiveAsync(gallery.Id, archivePath, TestContext.Current.CancellationToken);

        var before = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        var beforeFile = before.Files.Should().ContainSingle().Which;
        before.ImageCount.Should().Be(0);
        beforeFile.Path.Should().Be(archivePath);

        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var denyAll = await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Gallery,
            gallery.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "all"), TestContext.Current.CancellationToken);
        var initialJobIds = (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).ToList();
        var entityForbidden = () => AsUser(ApiTestUsers.Eva).RescanGalleryAsync(gallery.Id);
        await entityForbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(before);
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Equal(initialJobIds);
        await AsUser().DeleteEntityOverrideAsync(denyAll.Id, TestContext.Current.CancellationToken);

        var viewerUsername = $"gallery-rescan-viewer-{suffix}";
        const string viewerPassword = "Gallery rescan viewer 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var forbidden = () => viewerSession.Client.RescanGalleryAsync(gallery.Id);
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(before);
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Equal(initialJobIds);

        var jobId = await AsUser(ApiTestUsers.Eva).RescanGalleryAsync(gallery.Id, TestContext.Current.CancellationToken);
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        job.Status.Should().Be(JobStatus.Completed);
        job.Type.Should().Be("scan");
        job.Error.Should().BeNull();

        var rescanned = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        rescanned.Id.Should().Be(gallery.Id);
        rescanned.ImageCount.Should().Be(1);
        var rescannedFile = rescanned.Files.Should().ContainSingle().Which;
        rescannedFile.Id.Should().Be(beforeFile.Id);
        rescannedFile.Path.Should().Be(archivePath);
        rescannedFile.Size.Should().Be(new FileInfo(archivePath).Length);
        var importedImages = await AsUser().FindImagesAsync(new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter { GalleryId = gallery.Id },
            FindFilter = new FindFilter { Page = 1, PerPage = 10, Sort = "title" },
        }, TestContext.Current.CancellationToken);
        importedImages.TotalCount.Should().Be(1);
        var importedImage = importedImages.Items.Should().ContainSingle().Which;
        importedImage.Title.Should().Be("rescanned-image");
        importedImage.GalleryIds.Should().Equal(gallery.Id);
        var importedFile = importedImage.Files.Should().ContainSingle().Which;
        importedFile.Basename.Should().Be("rescanned-image.png");
        importedFile.Format.Should().Be("png");

        var emptyGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Gallery rescan empty {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var noFiles = () => AsUser(ApiTestUsers.Eva).RescanGalleryAsync(emptyGallery.Id);
        await noFiles.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var missing = () => AsUser(ApiTestUsers.Eva).RescanGalleryAsync(int.MaxValue);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(rescanned);
        var emptyAfterFailures = await AsUser().GetGalleryByIdAsync(emptyGallery.Id, TestContext.Current.CancellationToken);
        emptyAfterFailures.Files.Should().BeEmpty();
        emptyAfterFailures.ImageCount.Should().Be(0);
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(item => item.Id)
            .Should().Equal(new[] { job.Id }.Concat(initialJobIds));
    }
}
