using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Data;
using Cove.Data.Services;

using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

/// <summary>
/// An audio or text shows its tag links plus every tag a source recorded on it, so taking a scraped tag off
/// in the edit form, or in a bulk edit, drops the scraper's record too, as it does for a video; otherwise the
/// tag stays on the item as a locked, derived tag. Extension records stay.
/// </summary>
public class AudioTextTagEditProvenanceTests
{
    [Theory]
    [InlineData(AffinityHostType.Audio, false)]
    [InlineData(AffinityHostType.Audio, true)]
    [InlineData(AffinityHostType.Text, false)]
    [InlineData(AffinityHostType.Text, true)]
    public async Task RemovingAScrapedTagTakesItOffTheItem(AffinityHostType hostType, bool bulk)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"audio-text-tag-edits-{Guid.NewGuid():N}")
            .Options);
        var kept = new Tag { Name = "Kept" };
        var scraped = new Tag { Name = "Scraped" };
        var derived = new Tag { Name = "Added by an extension" };
        db.Tags.AddRange(kept, scraped, derived);
        BaseEntity entity = hostType == AffinityHostType.Audio
            ? db.Audios.Add(new Audio { Title = "Item", AudioTags = [new AudioTag { Tag = kept }, new AudioTag { Tag = scraped }], TagIds = [], PerformerIds = [] }).Entity
            : db.TextDocuments.Add(new TextDocument { Title = "Item", TextTags = [new TextTag { Tag = kept }, new TextTag { Tag = scraped }], TagIds = [], PerformerIds = [] }).Entity;
        await db.SaveChangesAsync(ct);
        var hostId = entity.Id;
        db.TagApplications.AddRange(
            new TagApplication { HostType = hostType, HostId = hostId, TagId = scraped.Id, SourceKey = "scraper:tests.fake-scraper/item", SourceRunId = "attempt-1" },
            new TagApplication { HostType = hostType, HostId = hostId, TagId = derived.Id, SourceKey = "ext:ai.tagging", SourceRunId = "run-1" });
        await db.SaveChangesAsync(ct);
        var customFields = new CustomFieldService(db);
        var tagProvenance = new TagProvenanceService(db);

        if (hostType == AffinityHostType.Audio)
        {
            var controller = new AudiosController(db, customFields, null!, null!, null!, tagProvenanceService: tagProvenance);
            if (bulk)
                await controller.BulkUpdate(new BulkAudioUpdateDto { Ids = [hostId], TagIds = [scraped.Id], TagMode = BulkUpdateMode.Remove }, ct);
            else
                await controller.Update(hostId, new AudioUpdateDto(null, null, null, null, null, null, null, [kept.Id], null, null, null), ct);
        }
        else
        {
            var controller = new TextsController(db, customFields, null!, null!, null!, null!, tagProvenanceService: tagProvenance);
            if (bulk)
                await controller.BulkUpdate(new BulkTextDocumentUpdateDto { Ids = [hostId], TagIds = [scraped.Id], TagMode = BulkUpdateMode.Remove }, ct);
            else
                await controller.Update(hostId, new TextDocumentUpdateDto(null, null, null, null, null, null, null, [kept.Id], null, null, null), ct);
        }

        var shownTagIds = await EffectiveHostTagQuery.ForHostType(db, hostType)
            .Where(row => row.HostId == hostId)
            .Select(row => row.TagId)
            .Distinct()
            .ToListAsync(ct);
        Assert.Equal(new[] { kept.Id, derived.Id }.Order(), shownTagIds.Order());
    }
}
