using Cove.Api.Controllers;
using Cove.Api.Http;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class BulkDeleteEventTargetTests
{
    [Fact]
    public async Task AudioBulkDeleteQueuesAllDistinctSelectedIds()
    {
        await using var db = CreateContext();
        var audio = new Audio { Title = "Audio" };
        db.Audios.Add(audio);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var jobs = new CapturingJobService();
        var controller = new AudiosController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            bulkDeletionJobService: CreateDeletionJobs(jobs));

        var result = controller.BulkDelete(new BatchDeleteDto([audio.Id, audio.Id, 999]), CancellationToken.None);

        AssertQueued(result, 2, "audio-bulk-delete", jobs);
    }

    [Fact]
    public async Task AudioBulkDeleteChecksPhysicalFilePermissionBeforeSelectionSize()
    {
        await using var db = CreateContext();
        var principal = MetadataDeleteOnlyPrincipal(Permissions.AudiosDelete);
        var controller = new AudiosController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            principalAccessor: principal);

        var result = controller.BulkDelete(
            new BatchDeleteDto([]) { DeleteFiles = true },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task AudioDeleteChecksPhysicalFilePermissionBeforeEntityLookup()
    {
        await using var db = CreateContext();
        var controller = new AudiosController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            principalAccessor: MetadataDeleteOnlyPrincipal(Permissions.AudiosDelete));

        var result = await controller.Delete(999, deleteFile: true, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ImageBulkDeleteQueuesAJob()
    {
        await using var db = CreateContext();
        var image = new Image { Title = "Image" };
        db.Images.Add(image);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var jobs = new CapturingJobService();
        var principal = new CurrentPrincipalAccessor();
        principal.Set(CovePrincipal.System());
        var controller = new ImagesController(
            new ImageRepository(db),
            db,
            new NoOpUserEngagementService(),
            new CustomFieldService(db),
            null!,
            null!,
            principalAccessor: principal,
            bulkDeletionJobService: CreateDeletionJobs(jobs));

        var result = controller.QueueBulkDelete(new BatchDeleteDto([image.Id, image.Id, 999]), CancellationToken.None);

        AssertQueued(result, 2, "image-bulk-delete", jobs);
    }

    [Fact]
    public async Task ImageBulkDeleteChecksFilePermissionBeforeSelectionSize()
    {
        await using var db = CreateContext();
        var principal = new CurrentPrincipalAccessor();
        principal.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "deleter",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.ImagesDelete },
        });
        var controller = new ImagesController(
            new ImageRepository(db),
            db,
            new NoOpUserEngagementService(),
            new CustomFieldService(db),
            null!,
            null!,
            principalAccessor: principal);

        var result = controller.QueueBulkDelete(
            new BatchDeleteDto([]) { DeleteFiles = true },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task TextBulkDeleteQueuesAllDistinctSelectedIds()
    {
        await using var db = CreateContext();
        var text = new TextDocument { Title = "Text" };
        db.TextDocuments.Add(text);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var jobs = new CapturingJobService();
        var controller = new TextsController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            null!,
            bulkDeletionJobService: CreateDeletionJobs(jobs));

        var result = controller.BulkDelete(new BatchDeleteDto([text.Id, text.Id, 999]), CancellationToken.None);

        AssertQueued(result, 2, "text-bulk-delete", jobs);
    }

    [Fact]
    public async Task TextBulkDeleteChecksPhysicalFilePermissionBeforeSelectionSize()
    {
        await using var db = CreateContext();
        var principal = MetadataDeleteOnlyPrincipal(Permissions.TextsDelete);
        var controller = new TextsController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            null!,
            principalAccessor: principal);

        var result = controller.BulkDelete(
            new BatchDeleteDto([]) { DeleteFiles = true },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task TextDeleteChecksPhysicalFilePermissionBeforeEntityLookup()
    {
        await using var db = CreateContext();
        var controller = new TextsController(
            db,
            new CustomFieldService(db),
            null!,
            null!,
            null!,
            null!,
            principalAccessor: MetadataDeleteOnlyPrincipal(Permissions.TextsDelete));

        var result = await controller.Delete(999, deleteFile: true, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    private static CurrentPrincipalAccessor MetadataDeleteOnlyPrincipal(string deletePermission)
    {
        var principal = new CurrentPrincipalAccessor();
        principal.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "metadata-deleter",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { deletePermission },
        });
        return principal;
    }

    private static BulkDeletionJobService CreateDeletionJobs(CapturingJobService jobs)
        => new(jobs, null!, new CoveConfiguration { MaxParallelTasks = 2 });

    private static void AssertQueued(IActionResult result, int itemCount, string type, CapturingJobService jobs)
    {
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        var queued = Assert.IsType<BulkDeletionJobStart>(accepted.Value);
        Assert.Equal("image-delete-job", queued.JobId);
        Assert.Equal(itemCount, queued.ItemCount);
        Assert.Equal(type, jobs.Type);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class CapturingJobService : Cove.Core.Interfaces.IJobService
    {
        public string? Type { get; private set; }

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Type = type;
            return "image-delete-job";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public Cove.Core.Interfaces.JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetAllJobs() => [];
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetJobHistory() => [];
    }
}
