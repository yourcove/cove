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

        // Blob ownership is a storage-integrity question, not a visibility question. The deletion
        // endpoint has already authorized its target; hiding another owner must never make a shared
        // blob look unreferenced.
        IQueryable<int> references = db.Set<Video>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1);
        references = references.Concat(db.Set<Audio>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<TextDocument>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Performer>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Performer>().IgnoreQueryFilters().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Studio>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Studio>().IgnoreQueryFilters().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Tag>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Tag>().IgnoreQueryFilters().Where(item => item.ImageOverrideBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Group>().IgnoreQueryFilters().Where(item => item.FrontImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Group>().IgnoreQueryFilters().Where(item => item.BackImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Gallery>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Gallery>().IgnoreQueryFilters().Where(item => item.BackImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Segment>().IgnoreQueryFilters().Where(item => item.ImageBlobId == blobId).Select(_ => 1));
        references = references.Concat(db.Set<Face>().IgnoreQueryFilters().Where(item => item.CoverBlobId == blobId).Select(_ => 1));

        return await references.Take(maximum).CountAsync(ct);
    }
}
