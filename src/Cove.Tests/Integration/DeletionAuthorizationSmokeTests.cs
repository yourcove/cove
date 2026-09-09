using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cove.Tests.Integration;

public sealed class DeletionAuthorizationSmokeTests
{
    [Fact]
    public async Task PhysicalDeletePermissionIsRejectedBeforeBulkEntityAuthorization()
    {
        var authorization = new CountingAuthorizationService();
        using var factory = new CoveWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(authorization);
        });
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/images/bulk")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    ids = Enumerable.Range(1, 10_000).ToArray(),
                    deleteFiles = true,
                    deleteGenerated = false,
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "integration-image-delete-without-file-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, authorization.EntityAuthorizationCalls);
        Assert.Contains(Permissions.ImagesDeleteFile, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParentVideoDeletionRequiresAccessToEveryDescendantBeforeTheActionRuns()
    {
        var authorization = new CountingAuthorizationService();
        using var factory = new CoveWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(authorization);
        });
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        var ids = await factory.WithDbContextAsync(async db =>
        {
            var parent = new Video { Title = "Deletion authorization parent" };
            var child = new Video { Title = "Deletion authorization child", ParentVideo = parent };
            db.AddRange(parent, child);
            await db.SaveChangesAsync();
            return (ParentId: parent.Id, ChildId: child.Id);
        });
        authorization.DeniedEntityId = ids.ChildId;
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync($"/api/videos/{ids.ParentId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ids.ParentId.ToString(), authorization.AuthorizedEntityIds);
        Assert.Contains(ids.ChildId.ToString(), authorization.AuthorizedEntityIds);
        Assert.True(await factory.WithDbContextAsync(async db =>
            await db.Videos.IgnoreQueryFilters().CountAsync(video => video.Id == ids.ParentId || video.Id == ids.ChildId) == 2));
    }

    [Fact]
    public async Task SingleParentVideoDeletionReauthorizesADescendantAttachedAfterFilterExpansion()
    {
        var authorization = new CountingAuthorizationService();
        using var factory = new CoveWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(authorization);
        });
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        var parentId = await factory.WithDbContextAsync(async db =>
        {
            var parent = new Video { Title = "Single deletion authorization parent" };
            db.Videos.Add(parent);
            await db.SaveChangesAsync();
            return parent.Id;
        });
        var attached = 0;
        var childId = 0;
        authorization.BeforeAuthorizeAsync = async entity =>
        {
            if (entity.Id != parentId.ToString() || Interlocked.Exchange(ref attached, 1) != 0)
                return;

            childId = await factory.WithDbContextAsync(async db =>
            {
                var child = new Video
                {
                    Title = "Late single deletion authorization child",
                    ParentVideoId = parentId,
                };
                db.Videos.Add(child);
                await db.SaveChangesAsync();
                return child.Id;
            });
            authorization.DeniedEntityId = childId;
        };
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync($"/api/videos/{parentId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(childId > 0);
        Assert.Contains(childId.ToString(), authorization.AuthorizedEntityIds);
        Assert.True(await factory.WithDbContextAsync(async db =>
            await db.Videos.IgnoreQueryFilters().CountAsync(video => video.Id == parentId || video.Id == childId) == 2));
    }

    [Fact]
    public async Task QueuedParentVideoDeletionReauthorizesADescendantAttachedBeforeExecution()
    {
        var authorization = new CountingAuthorizationService();
        var jobs = new CapturingJobService();
        using var factory = new CoveWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(authorization);
            services.RemoveAll<IJobService>();
            services.AddSingleton<IJobService>(jobs);
        });
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        var parentId = await factory.WithDbContextAsync(async db =>
        {
            var parent = new Video { Title = "Queued deletion authorization parent" };
            db.Videos.Add(parent);
            await db.SaveChangesAsync();
            return parent.Id;
        });
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/videos/destroy", new
        {
            ids = new[] { parentId },
            deleteFiles = false,
            deleteGenerated = false,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var childId = await factory.WithDbContextAsync(async db =>
        {
            var child = new Video
            {
                Title = "Late deletion authorization child",
                ParentVideoId = parentId,
            };
            db.Videos.Add(child);
            await db.SaveChangesAsync();
            return child.Id;
        });
        authorization.DeniedEntityId = childId;

        await jobs.RunAsync();

        Assert.Contains(childId.ToString(), authorization.AuthorizedEntityIds);
        Assert.True(await factory.WithDbContextAsync(async db =>
            await db.Videos.IgnoreQueryFilters().CountAsync(video => video.Id == parentId || video.Id == childId) == 2));
    }

    private sealed class CountingAuthorizationService : IAuthorizationService
    {
        public int EntityAuthorizationCalls;
        public int? DeniedEntityId { get; set; }
        public List<string> AuthorizedEntityIds { get; } = [];
        public Func<EntityRef, Task>? BeforeAuthorizeAsync { get; set; }

        public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
        {
            if (entity.HasValue)
            {
                Interlocked.Increment(ref EntityAuthorizationCalls);
                lock (AuthorizedEntityIds)
                    AuthorizedEntityIds.Add(entity.Value.Id);
                if (entity.Value.Id == DeniedEntityId?.ToString())
                    return AuthorizationResult.Deny("The descendant is outside the caller's deletion scope.", permission);
            }
            return AuthorizationResult.Allow();
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            CovePrincipal? principal,
            string permission,
            EntityRef? entity,
            CancellationToken ct)
            => AuthorizeAfterHookAsync(principal, permission, entity);

        public async Task<IReadOnlyList<AuthorizationResult>> AuthorizeManyAsync(
            CovePrincipal? principal,
            string permission,
            IReadOnlyList<EntityRef> entities,
            CancellationToken ct)
        {
            var results = new List<AuthorizationResult>(entities.Count);
            foreach (var entity in entities)
                results.Add(await AuthorizeAfterHookAsync(principal, permission, entity));
            return results;
        }

        private async Task<AuthorizationResult> AuthorizeAfterHookAsync(
            CovePrincipal? principal,
            string permission,
            EntityRef? entity)
        {
            if (entity.HasValue && BeforeAuthorizeAsync is not null)
                await BeforeAuthorizeAsync(entity.Value);
            return Authorize(principal, permission, entity);
        }

        public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        {
        }

        public bool Has(CovePrincipal? principal, string permission) => true;
    }

    private sealed class CapturingJobService : IJobService
    {
        private Func<IJobProgress, CancellationToken, Task>? _work;

        public string EnqueueOwned(
            JobOwner owner,
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            string? resultUrl = null,
            bool exclusive = true)
        {
            _work = work;
            return "captured-deletion-job";
        }

        public string Enqueue(
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            bool exclusive = true)
        {
            _work = work;
            return "captured-deletion-job";
        }

        public Task RunAsync()
            => (_work ?? throw new InvalidOperationException("No deletion job was queued."))(
                new NullProgress(),
                CancellationToken.None);

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }
}
