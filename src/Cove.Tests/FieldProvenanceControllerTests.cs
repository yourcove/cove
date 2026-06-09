using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class FieldProvenanceControllerTests
{
    [Fact]
    public async Task FacesController_GetById_IncludesFieldProvenance()
    {
        var dbName = $"field-provenance-face-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var face = new Face { Label = "Candidate", PrimarySourceKey = "ext:test" };
        db.Faces.Add(face);
        await db.SaveChangesAsync();

        var fieldProvenance = new FieldProvenanceService(db);
        await fieldProvenance.RecordAsync(AffinityHostType.Face, face.Id, "label", "Candidate", "metadata:test", cancellationToken: CancellationToken.None);
        await db.SaveChangesAsync();

        var controller = new FacesController(
            db,
            null!,
            null!,
            null!,
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance,
            faceSuggesters: [],
            fieldProvenanceService: fieldProvenance);

        var result = await controller.GetById(face.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<FaceDto>(ok.Value);

        Assert.Contains(dto.FieldProvenance ?? [], item => item.FieldKey == "label" && item.SourceKey == "metadata:test");
    }

    [Fact]
    public async Task SegmentsController_GetById_IncludesFieldProvenance()
    {
        var dbName = $"field-provenance-segment-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var video = new Video { Title = "Video" };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            StartSec = 12.5,
            EndSec = 24,
            SourceKey = "ext:test",
            Title = "Detected beat",
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        var fieldProvenance = new FieldProvenanceService(db);
        await fieldProvenance.RecordAsync(AffinityHostType.Segment, segment.Id, "title", "Detected beat", "ext:test", cancellationToken: CancellationToken.None);
        await db.SaveChangesAsync();

        var controller = new SegmentsController(db, null!, fieldProvenance);

        var result = await controller.GetById(segment.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SegmentRecordDto>(ok.Value);

        Assert.Contains(dto.FieldProvenance ?? [], item => item.FieldKey == "title" && item.SourceKey == "ext:test");
    }

    private static CoveContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new CoveContext(options);
    }
}
