using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public interface IBlobReferenceCounter
{
    Task<int> CountReferencesAsync(string blobId, int maximum, CancellationToken ct = default);
}

public sealed class BlobReferenceCounter(IServiceScopeFactory scopeFactory) : IBlobReferenceCounter
{
    public async Task<int> CountReferencesAsync(string blobId, int maximum, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        IQueryable<int> references = db.Set<Video>().Where(item => item.ImageBlobId == blobId).Select(_ => 1);
        references = references.Concat(db.Set<Audio>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<TextDocument>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Performer>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Performer>().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Studio>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Studio>().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Tag>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Tag>().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Group>().Where(item => item.FrontImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Group>().Where(item => item.BackImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Gallery>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Gallery>().Where(item => item.BackImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Segment>().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Face>().Where(item => item.CoverBlobId == blobId).Select(_ => 1));

        return await references.Take(maximum).CountAsync(ct);
    }
}
