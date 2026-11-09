using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class BlobReferenceCounterTests
{
    [Fact]
    public async Task CountReferencesAsync_CountsDistinctReferenceSlotsAndHonorsMaximum()
    {
        const string blobId = "11111111-1111-4111-8111-111111111111";
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
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

    // Every column BlobReferenceCounter checks whose owner has an authorization query filter. Segments have none,
    // so a segment reference is counted whether or not the counter ignores query filters.
    public static TheoryData<string> ReferenceColumns =>
    [
        "video.image", "audio.image", "text.image",
        "performer.image", "performer.override", "studio.image", "studio.override",
        "tag.image", "tag.override", "group.front", "group.back",
        "gallery.image", "gallery.back", "face.cover",
    ];

    [Theory]
    [MemberData(nameof(ReferenceColumns))]
    public async Task CountReferencesAsync_CountsOwnersHiddenFromTheCurrentPrincipal(string column)
    {
        const string blobId = "22222222-2222-4222-8222-222222222222";
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        var principalAccessor = new CurrentPrincipalAccessor();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options, principalAccessor));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await SeedOwnerAsync(db, column, blobId);
        }

        principalAccessor.Set(new CovePrincipal
        {
            UserId = 7,
            Username = "metadata-only-deleter",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string>(),
            Roles = new HashSet<string>(),
        });
        await using (var scope = provider.CreateAsyncScope())
        {
            // The owner must really be hidden from this user, or the count below would pass without the bypass.
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.False(await IsOwnerVisibleAsync(db, column));
        }
        var counter = new BlobReferenceCounter(provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, await counter.CountReferencesAsync(blobId, maximum: 2, ct: TestContext.Current.CancellationToken));
    }

    private static async Task SeedOwnerAsync(CoveContext db, string column, string blobId)
    {
        var ct = TestContext.Current.CancellationToken;
        switch (column)
        {
            case "video.image": db.Videos.Add(new Video { Title = "owner", ImageBlobId = blobId }); break;
            case "audio.image": db.Audios.Add(new Audio { Title = "owner", ImageBlobId = blobId }); break;
            case "text.image": db.TextDocuments.Add(new TextDocument { Title = "owner", ImageBlobId = blobId }); break;
            case "performer.image": db.Performers.Add(new Performer { Name = "owner", ImageBlobId = blobId }); break;
            case "performer.override": db.Performers.Add(new Performer { Name = "owner", ImageOverrideBlobId = blobId }); break;
            case "studio.image": db.Studios.Add(new Studio { Name = "owner", ImageBlobId = blobId }); break;
            case "studio.override": db.Studios.Add(new Studio { Name = "owner", ImageOverrideBlobId = blobId }); break;
            case "tag.image": db.Tags.Add(new Tag { Name = "owner", ImageBlobId = blobId }); break;
            case "tag.override": db.Tags.Add(new Tag { Name = "owner", ImageOverrideBlobId = blobId }); break;
            case "group.front": db.Groups.Add(new Group { Name = "owner", FrontImageBlobId = blobId }); break;
            case "group.back": db.Groups.Add(new Group { Name = "owner", BackImageBlobId = blobId }); break;
            case "gallery.image": db.Galleries.Add(new Gallery { Title = "owner", ImageBlobId = blobId }); break;
            case "gallery.back": db.Galleries.Add(new Gallery { Title = "owner", BackImageBlobId = blobId }); break;
            case "face.cover": db.Faces.Add(new Face { Label = "owner", CoverBlobId = blobId }); break;
            default: throw new ArgumentOutOfRangeException(nameof(column), column, null);
        }
        await db.SaveChangesAsync(ct);
    }

    private static Task<bool> IsOwnerVisibleAsync(CoveContext db, string column)
    {
        var ct = TestContext.Current.CancellationToken;
        return column.Split('.')[0] switch
        {
            "video" => db.Videos.AnyAsync(item => item.Title == "owner", ct),
            "audio" => db.Audios.AnyAsync(ct),
            "text" => db.TextDocuments.AnyAsync(ct),
            "performer" => db.Performers.AnyAsync(ct),
            "studio" => db.Studios.AnyAsync(ct),
            "tag" => db.Tags.AnyAsync(ct),
            "group" => db.Groups.AnyAsync(ct),
            "gallery" => db.Galleries.AnyAsync(ct),
            "face" => db.Faces.AnyAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
    }
}
