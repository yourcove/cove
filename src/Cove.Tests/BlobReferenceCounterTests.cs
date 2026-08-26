using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public sealed class BlobReferenceCounterTests
{
    [Fact]
    public async Task CountReferencesAsync_CountsDistinctReferenceSlotsAndHonorsMaximum()
    {
        const string blobId = "11111111-1111-4111-8111-111111111111";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Videos.Add(new Video { ImageBlobId = blobId });
            db.Performers.Add(new Performer
            {
                Name = "fixture",
                ImageBlobId = blobId,
                ImageOverrideBlobId = blobId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var counter = new BlobReferenceCounter(provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(2, await counter.CountReferencesAsync(blobId, maximum: 2, ct: TestContext.Current.CancellationToken));
        Assert.Equal(3, await counter.CountReferencesAsync(blobId, maximum: 10, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CountReferencesAsync_CountsOwnersHiddenFromTheCurrentPrincipal()
    {
        const string blobId = "22222222-2222-4222-8222-222222222222";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        var principalAccessor = new CurrentPrincipalAccessor();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options, principalAccessor));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync();
            db.Videos.Add(new Video { Title = "Hidden blob owner", ImageBlobId = blobId });
            await db.SaveChangesAsync();
        }

        principalAccessor.Set(new CovePrincipal
        {
            UserId = 7,
            Username = "metadata-only-deleter",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string>(),
            Roles = new HashSet<string>(),
        });
        var counter = new BlobReferenceCounter(provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, await counter.CountReferencesAsync(blobId, maximum: 2));
    }
}
