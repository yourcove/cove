using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class AiCoreControllerTests
{
    [Fact]
    public async Task FacesController_CanCreateUpdateLinkMergeAndFindSimilarFaces()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var performer = new Performer { Name = "Alex" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var controller = new FacesController(
            context,
            embeddingService,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var firstCreate = await controller.Create(new FaceCreateDto("Lead", null, false, "ext:ai.faces"), CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedAtActionResult>(firstCreate.Result);
        var firstFace = Assert.IsType<FaceDto>(firstCreated.Value);

        var secondCreate = await controller.Create(new FaceCreateDto("Support", null, false, "ext:ai.faces"), CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedAtActionResult>(secondCreate.Result);
        var secondFace = Assert.IsType<FaceDto>(secondCreated.Value);

        var updateResult = await controller.Update(firstFace.Id, new FaceUpdateDto("Lead Updated", performer.Id, false, "user"), CancellationToken.None);
        var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedFace = Assert.IsType<FaceDto>(updateOk.Value);
        Assert.Equal("Lead Updated", updatedFace.Label);
        Assert.Equal(performer.Id, updatedFace.PerformerId);
        Assert.Equal("Alex", updatedFace.PerformerName);

        var linkResult = await controller.Link(secondFace.Id, new FaceLinkDto(performer.Id), CancellationToken.None);
        var linkOk = Assert.IsType<OkObjectResult>(linkResult.Result);
        var linkedFace = Assert.IsType<FaceDto>(linkOk.Value);
        Assert.Equal(performer.Id, linkedFace.PerformerId);

        context.Embeddings.AddRange(
            new Embedding
            {
                HostType = EmbeddingHostType.Face,
                HostId = firstFace.Id,
                Kind = "face.arcface",
                KindFamily = "face.arcface",
                Modality = EmbeddingModality.Face,
                IsSemantic = true,
                Dim = 3,
                Vector = new Vector(new[] { 1f, 0f, 0f }),
                SourceKey = "ext:ai.faces",
            },
            new Embedding
            {
                HostType = EmbeddingHostType.Face,
                HostId = secondFace.Id,
                Kind = "face.arcface",
                KindFamily = "face.arcface",
                Modality = EmbeddingModality.Face,
                IsSemantic = true,
                Dim = 3,
                Vector = new Vector(new[] { 0.95f, 0.05f, 0f }),
                SourceKey = "ext:ai.faces",
            });
        var hostImage = new Image { Title = "Still" };
        context.Images.Add(hostImage);
        await context.SaveChangesAsync();

        var representativeDetection = new Detection
        {
            HostType = DetectionHostType.Image,
            HostId = hostImage.Id,
            FrameWidth = 1200,
            FrameHeight = 1600,
            Class = "face",
            Score = 0.92f,
            X = 120,
            Y = 180,
            W = 240,
            H = 300,
            RefKind = "face",
            RefId = secondFace.Id,
            SourceKey = "ext:ai.faces",
        };
        context.Detections.Add(representativeDetection);
        await context.SaveChangesAsync();

        var similarResult = await controller.GetSimilar(firstFace.Id, "face.arcface", null, null, null, 1, 5, 5, CancellationToken.None);
        var similarOk = Assert.IsType<OkObjectResult>(similarResult.Result);
        var similarFaces = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(similarOk.Value);
        var match = Assert.Single(similarFaces.Items);
        Assert.Equal(secondFace.Id, match.Id);
        Assert.Contains($"/api/stream/detection/{representativeDetection.Id}/crop", match.CoverImageUrl, StringComparison.Ordinal);

        var mergeResult = await controller.MergeInto(secondFace.Id, new FaceMergeDto(firstFace.Id), CancellationToken.None);
        var mergeOk = Assert.IsType<OkObjectResult>(mergeResult.Result);
        var mergedFace = Assert.IsType<FaceDto>(mergeOk.Value);
        Assert.Equal(firstFace.Id, mergedFace.MergedIntoFaceId);

        var ignoreResult = await controller.SetIgnored(firstFace.Id, new FaceIgnoreDto(true), CancellationToken.None);
        var ignoreOk = Assert.IsType<OkObjectResult>(ignoreResult.Result);
        var ignoredFace = Assert.IsType<FaceDto>(ignoreOk.Value);
        Assert.True(ignoredFace.Ignored);
    }

    [Fact]
    public async Task FacesController_GetSimilar_HonorsExactCandidateLimitAcrossPages()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var sourceFace = new Face { Label = "Source", PrimarySourceKey = "ext:ai.faces" };
        context.Faces.Add(sourceFace);
        await context.SaveChangesAsync();

        var candidateFaces = Enumerable.Range(1, 8)
            .Select(index => new Face { Label = $"Candidate {index}", PrimarySourceKey = $"candidate:{index}" })
            .ToArray();
        context.Faces.AddRange(candidateFaces);
        await context.SaveChangesAsync();

        context.Embeddings.Add(new Embedding
        {
            HostType = EmbeddingHostType.Face,
            HostId = sourceFace.Id,
            Kind = "face.arcface",
            KindFamily = "face.arcface",
            Modality = EmbeddingModality.Face,
            IsSemantic = true,
            Dim = 3,
            Vector = new Vector(new[] { 1f, 0f, 0f }),
            SourceKey = "ext:ai.faces",
        });
        context.Embeddings.AddRange(candidateFaces.Select((face, index) => new Embedding
        {
            HostType = EmbeddingHostType.Face,
            HostId = face.Id,
            Kind = "face.arcface",
            KindFamily = "face.arcface",
            Modality = EmbeddingModality.Face,
            IsSemantic = true,
            Dim = 3,
            Vector = new Vector(new[] { 1f, (index + 1) * 0.1f, 0f }),
            SourceKey = "ext:ai.faces",
        }));
        await context.SaveChangesAsync();

        var controller = new FacesController(
            context,
            new EmbeddingService(context, []),
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance);

        var firstResult = await controller.GetSimilar(sourceFace.Id, "face.arcface", null, null, null, 1, 5, 5, CancellationToken.None);
        var firstOk = Assert.IsType<OkObjectResult>(firstResult.Result);
        var firstPage = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(firstOk.Value);
        var result = await controller.GetSimilar(sourceFace.Id, "face.arcface", null, null, null, 2, 2, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PaginatedResponse<FaceSimilarDto>>(ok.Value);

        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(firstPage.Items.Skip(2).Take(2).Select(face => face.Id), page.Items.Select(face => face.Id));
    }

    [Fact]
    public async Task FacesController_CanListDetectionsPointingToFace()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var image = new Image { Title = "Still" };
        var video = new Video { Title = "Clip" };
        context.Faces.Add(face);
        context.Images.Add(image);
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.Detections.AddRange(
            new Detection
            {
                HostType = DetectionHostType.Image,
                HostId = image.Id,
                FrameWidth = 1200,
                FrameHeight = 1600,
                Class = "face",
                Score = 0.92f,
                X = 120,
                Y = 180,
                W = 240,
                H = 300,
                RefKind = "face",
                RefId = face.Id,
                SourceKey = "ext:ai.faces",
            },
            new Detection
            {
                HostType = DetectionHostType.Video,
                HostId = video.Id,
                ObservedAtSec = 33.5,
                FrameWidth = 1920,
                FrameHeight = 1080,
                Class = "face",
                Score = 0.89f,
                X = 640,
                Y = 160,
                W = 220,
                H = 260,
                RefKind = "face",
                RefId = face.Id,
                SourceKey = "ext:ai.faces",
            });
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var controller = new FacesController(
            context,
            embeddingService,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance);

        var result = await controller.GetDetections(face.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detections = Assert.IsAssignableFrom<IReadOnlyList<DetectionDto>>(ok.Value);
        Assert.Equal(2, detections.Count);
        Assert.Contains(detections, detection => detection.HostType == DetectionHostType.Image && detection.HostId == image.Id);
        Assert.Contains(detections, detection => detection.HostType == DetectionHostType.Video && detection.HostId == video.Id);
    }

    [Fact]
    public async Task FacePerformerPropagation_RecordsAiFacePerformerFieldProvenance()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var performer = new Performer { Name = "Alex" };
        var video = new Video { Title = "Clip" };
        var face = new Face { Label = "Alex Face", PrimarySourceKey = "ext:ai.faces" };
        context.AddRange(performer, video, face);
        await context.SaveChangesAsync();

        context.FaceAppearances.Add(new FaceAppearance
        {
            FaceId = face.Id,
            HostType = FaceAppearanceHostType.Video,
            HostId = video.Id,
            SourceKey = "ext:ai.faces",
            SourceRunId = "run-1",
            TopConfidence = 0.92f,
        });
        await context.SaveChangesAsync();

        var fieldProvenance = new FieldProvenanceService(context);
        var propagation = new FacePerformerPropagationService(context, fieldProvenance);

        await propagation.ApplyLinkChangeAsync(face.Id, null, performer.Id, CancellationToken.None);
        await context.SaveChangesAsync();

        Assert.True(await context.Set<VideoPerformer>().AnyAsync(item => item.VideoId == video.Id && item.PerformerId == performer.Id));

        var rows = await fieldProvenance.GetForHostAsync(AffinityHostType.Video, video.Id);
        var performers = Assert.Single(rows, row => row.FieldKey == "performers");
        Assert.Equal("ext:ai.faces", performers.SourceKey);
        Assert.Equal("run-1", performers.SourceRunId);
        Assert.Equal(0.92f, performers.Confidence);
        Assert.True(performers.Value.HasValue);
        Assert.Contains(performers.Value.Value.EnumerateArray(), value => value.GetString() == "Alex");
    }

    [Fact]
    public async Task FacesController_GetById_UsesCanonicalFaceImageRouteWhenCoverExists()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face
        {
            Label = "Lead",
            CoverBlobId = "blob-1",
            PrimarySourceKey = "ext:ai.faces",
        };
        context.Faces.Add(face);
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var controller = new FacesController(
            context,
            embeddingService,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance);

        var result = await controller.GetById(face.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<FaceDto>(ok.Value);

        Assert.NotNull(dto.CoverImageUrl);
        Assert.StartsWith($"/api/faces/{face.Id}/image?max=640&v=", dto.CoverImageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/entity-images/", dto.CoverImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FacesController_GetById_PropagatesBearerTokenIntoCoverImageUrl()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face
        {
            Label = "Lead",
            CoverBlobId = "blob-1",
            PrimarySourceKey = "ext:ai.faces",
        };
        context.Faces.Add(face);
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer owner-token";
        var controller = new FacesController(
            context,
            embeddingService,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };

        var result = await controller.GetById(face.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<FaceDto>(ok.Value);

        Assert.NotNull(dto.CoverImageUrl);
        Assert.Contains("access_token=owner-token", dto.CoverImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityImageController_GetFaceImage_ReturnsStoredBlob()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var bytes = new byte[] { 1, 2, 3, 4 };

        var face = new Face
        {
            Label = "Lead",
            CoverBlobId = "blob-1",
            PrimarySourceKey = "ext:ai.faces",
        };
        context.Faces.Add(face);
        await context.SaveChangesAsync();

        var controller = new EntityImageController(
            context,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>
            {
                ["blob-1"] = (bytes, "image/jpeg"),
            }),
            new StubThumbnailService(),
            new StubStreamService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.GetFaceImage(face.Id, null, null, CancellationToken.None);
        var file = Assert.IsType<FileStreamResult>(result);

        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("public, max-age=3600", controller.Response.Headers.CacheControl.ToString());
        await using var output = new MemoryStream();
        await file.FileStream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
    }


    [Fact]
    public async Task EntityImageController_GetVideoImage_ReturnsStreamScreenshotWhenNoStoredBlob()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var bytes = new byte[] { 9, 8, 7, 6 };

        var video = new Video { Title = "Video without custom cover" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var controller = new EntityImageController(
            context,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new StubThumbnailService(),
            new StubStreamService(bytes, "image/webp", useLongCache: true))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.GetVideoImage(video.Id, null, null, CancellationToken.None);
        var file = Assert.IsType<FileStreamResult>(result);

        Assert.Equal("image/webp", file.ContentType);
        Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task FacesController_GetSuggestions_ReturnsEmptyListWhenNoSuggestersRegistered()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face
        {
            Label = "Lead",
            PrimarySourceKey = "ext:ai.faces",
        };
        context.Faces.Add(face);
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var controller = new FacesController(
            context,
            embeddingService,
            new StubBlobService(new Dictionary<string, (byte[] Bytes, string ContentType)>()),
            new FacePerformerPropagationService(context),
            Array.Empty<IFaceLifecycleParticipant>(),
            NullLogger<FacesController>.Instance);

        var result = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task EmbeddingsController_CanListAndSearchEmbeddingsUsingSQLiteFallback()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Embeddings.AddRange(
            new Embedding
            {
                HostType = EmbeddingHostType.Video,
                HostId = 11,
                Kind = "video.clip",
                KindFamily = "video.clip",
                Modality = EmbeddingModality.Visual,
                IsSemantic = true,
                Dim = 2,
                Vector = new Vector(new[] { 1f, 0f }),
                SourceKey = "ext:ai.clip",
                SectionIndex = 0,
            },
            new Embedding
            {
                HostType = EmbeddingHostType.Video,
                HostId = 12,
                Kind = "video.clip",
                KindFamily = "video.clip",
                Modality = EmbeddingModality.Visual,
                IsSemantic = true,
                Dim = 2,
                Vector = new Vector(new[] { 0f, 1f }),
                SourceKey = "ext:ai.clip",
                SectionIndex = 1,
            });
        await context.SaveChangesAsync();

        var embeddingService = new EmbeddingService(context, []);
        var controller = new EmbeddingsController(context, embeddingService, embeddingService);

        var listResult = await controller.List(EmbeddingHostType.Video, null, null, "video.clip", null, null, 1, 20, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var list = Assert.IsType<PaginatedResponse<EmbeddingDto>>(listOk.Value);
        Assert.Equal(2, list.TotalCount);

        var searchResult = await controller.Search(
            new EmbeddingSearchRequestDto(
                QueryText: null,
                QueryVector: [0.9f, 0.1f],
                Kind: null,
                KindFamily: "video.clip",
                HostType: EmbeddingHostType.Video,
                HostId: null,
                Modality: EmbeddingModality.Visual,
                IsSemantic: true,
                SourceKey: "ext:ai.clip",
                K: 2),
            CancellationToken.None);

        var searchOk = Assert.IsType<OkObjectResult>(searchResult.Result);
        var matches = Assert.IsAssignableFrom<IReadOnlyList<EmbeddingSearchResultDto>>(searchOk.Value);
        Assert.Equal(2, matches.Count);
        Assert.Equal(11, matches[0].HostId);
        Assert.True(matches[0].Distance < matches[1].Distance);
    }

    [Fact]
    public async Task AiRunsController_CanListAndGetRunProvenance()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.AiRuns.AddRange(
            new AiRun
            {
                RunKey = "run-a",
                SourceKey = "ext:ai.faces",
                TargetType = AiRunTargetType.Video,
                TargetId = 10,
                Trigger = "manual",
                JobId = "job-1",
                Status = AiRunStatus.Completed,
                StartedAt = DateTime.UtcNow.AddMinutes(-2),
                CompletedAt = DateTime.UtcNow.AddMinutes(-1),
                Summary = JsonDocument.Parse("{" + "\"faces\":4}"),
            },
            new AiRun
            {
                RunKey = "run-b",
                SourceKey = "ext:ai.clip",
                TargetType = AiRunTargetType.Image,
                TargetId = 20,
                Trigger = "scheduled",
                Status = AiRunStatus.Running,
                StartedAt = DateTime.UtcNow,
            });
        await context.SaveChangesAsync();

        var controller = new AiRunsController(context);

        var listResult = await controller.List(AiRunTargetType.Video, 10, "ext:ai.faces", null, AiRunStatus.Completed, 1, 20, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var list = Assert.IsType<PaginatedResponse<AiRunDto>>(listOk.Value);
        var run = Assert.Single(list.Items);
        Assert.Equal("run-a", run.RunKey);
        Assert.True(run.Summary.HasValue);
        Assert.Equal(4, run.Summary.Value.GetProperty("faces").GetInt32());

        var getResult = await controller.GetById(run.Id, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var fetched = Assert.IsType<AiRunDto>(getOk.Value);
        Assert.Equal(run.Id, fetched.Id);
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new AiCoreTestContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection);
    }

    private sealed class AiCoreTestContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class StubBlobService(Dictionary<string, (byte[] Bytes, string ContentType)> blobs) : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
        {
            if (!blobs.TryGetValue(blobId, out var blob))
                return Task.FromResult<(Stream, string)?>(null);

            return Task.FromResult<(Stream, string)?>(
                (new MemoryStream(blob.Bytes, writable: false), blob.ContentType));
        }

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubThumbnailService : IThumbnailService
    {
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetThumbnailPathForVideo(int videoId)
            => throw new NotSupportedException();

        public string GetTimestampedThumbnailPath(int videoId, double seconds)
            => throw new NotSupportedException();

        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds)
            => throw new NotSupportedException();

        public string GetPreviewPath(int videoId)
            => throw new NotSupportedException();

        public string GetSpritePath(int videoId)
            => throw new NotSupportedException();

        public string GetSpriteVttPath(int videoId)
            => throw new NotSupportedException();

        public string StartGenerateAllThumbnails()
            => throw new NotSupportedException();
    }

    private sealed class StubStreamService(byte[]? screenshotBytes = null, string screenshotContentType = "image/jpeg", bool useLongCache = false) : IStreamService
    {
        public Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(int videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(int videoId, double? seconds, CancellationToken ct = default)
        {
            if (screenshotBytes == null)
                throw new NotSupportedException();

            return Task.FromResult<(Stream, string, bool)?>(
                (new MemoryStream(screenshotBytes, writable: false), screenshotContentType, useLongCache));
        }

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetSegmentAnimatedPreview(int videoId, double seconds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

