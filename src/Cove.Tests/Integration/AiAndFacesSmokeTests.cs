using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Cove.Tests.Integration;

public sealed class AiDataControllerSmokeTests
{
    [Fact]
    public async Task Summary_ReturnsOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Audio Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();

            db.AiRuns.Add(new AiRun
            {
                RunKey = "run-summary",
                SourceKey = "ext:ai.audio",
                TargetType = AiRunTargetType.Video,
                TargetId = video.Id,
                Models = JsonDocument.Parse("[{\"ConfigName\":\"audio-model\"}]"),
            });
            db.Segments.Add(new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                StartSec = 0,
                EndSec = 3,
                Kind = "audio.label",
                SourceKey = "ext:ai.audio",
                SourceRunId = "run-summary",
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/ai-data/summary", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadApiJsonAsync<AiDataSummaryDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.NotEmpty(summary.Items);
    }
}

public sealed class AiRunsControllerSmokeTests
{
    [Fact]
    public async Task List_ReturnsOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            db.AiRuns.Add(new AiRun
            {
                RunKey = "run-a",
                SourceKey = "ext:ai.faces",
                TargetType = AiRunTargetType.Video,
                TargetId = 10,
                Trigger = "manual",
                Status = AiRunStatus.Completed,
                StartedAt = DateTime.UtcNow.AddMinutes(-2),
                CompletedAt = DateTime.UtcNow.AddMinutes(-1),
                Summary = JsonDocument.Parse("{\"faces\":4}"),
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/ai-runs?page=1&perPage=10", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<PaginatedResponse<AiRunDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Single(payload.Items);
    }
}

public sealed class EmbeddingsControllerSmokeTests
{
    [Fact]
    public async Task List_ReturnsOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Embedding Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();

            db.Embeddings.Add(new Embedding
            {
                HostType = EmbeddingHostType.Video,
                HostId = video.Id,
                Kind = "video.clip",
                KindFamily = "video.clip",
                Modality = EmbeddingModality.Visual,
                Dim = 2,
                Vector = new Vector(new float[] { 0.1f, 0.2f }),
                SourceKey = "ext:ai.visual",
                SourceRunId = "run-1",
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/embeddings?page=1&perPage=10", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<PaginatedResponse<EmbeddingDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Single(payload.Items);
    }
}

public sealed class FacesControllerSmokeTests
{
    [Fact]
    public async Task List_And_Suggestions_ReturnOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var faceId = await factory.WithDbContextAsync(async db =>
        {
            var face = new Face
            {
                Label = "Lead",
                PrimarySourceKey = "ext:ai.faces",
            };
            db.Faces.Add(face);
            await db.SaveChangesAsync();
            return face.Id;
        });

        using var client = factory.CreateAuthenticatedClient();

        var listResponse = await client.GetAsync("/api/faces?page=1&perPage=10", TestContext.Current.CancellationToken);
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await listResponse.Content.ReadApiJsonAsync<PaginatedResponse<FaceDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(listPayload);
        Assert.Single(listPayload.Items);

        var suggestionsResponse = await client.GetAsync($"/api/faces/{faceId}/suggestions?maxResults=5", TestContext.Current.CancellationToken);
        suggestionsResponse.EnsureSuccessStatusCode();
        var suggestions = await suggestionsResponse.Content.ReadApiJsonAsync<List<FaceSuggestionDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(suggestions);
    }
}
