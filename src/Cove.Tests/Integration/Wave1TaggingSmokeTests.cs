using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

public sealed class Wave1TaggingSmokeTests
{
    [Fact]
    public async Task TagSearch_WithQuery_ReturnsMatchingItems()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            db.Tags.AddRange(
                new Tag { Name = "Searchable Squirting" },
                new Tag { Name = "Unrelated Tag", Description = "Does not match" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/tags?q=squirt&perPage=10&sort=name&direction=asc", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Contains(items, item => item.GetProperty("name").GetString() == "Searchable Squirting");
    }

    [Fact]
    public async Task VideoSearch_WithQuery_ReturnsMatchingItems()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            db.Videos.AddRange(
                new Video { Title = "Searchable Squirt Video", Details = "Contains the search term" },
                new Video { Title = "Other Video", Details = "Different content" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/videos?q=squirt&perPage=10&sort=title&direction=asc", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Contains(items, item => item.GetProperty("title").GetString() == "Searchable Squirt Video");
    }

    [Theory]
    [InlineData("Performer Match Video", "maria")]
    [InlineData("Performer Alias Video", "stage-name")]
    [InlineData("Tag Match Video", "tagged")]
    [InlineData("Tag Alias Video", "alias-tag")]
    public async Task VideoSearch_WithRelatedEntityQuery_ReturnsMatchingItems(string expectedTitle, string query)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            var performer = new Performer
            {
                Name = "Melena Maria Rya",
                Aliases = { new PerformerAlias { Alias = "Stage-Name Search" } },
            };
            var tag = new Tag
            {
                Name = "Tagged Relation",
                Aliases = { new TagAlias { Alias = "Alias-Tag Search" } },
            };

            db.Videos.AddRange(
                new Video { Title = "Performer Match Video", VideoPerformers = { new VideoPerformer { Performer = performer } } },
                new Video { Title = "Performer Alias Video", VideoPerformers = { new VideoPerformer { Performer = performer } } },
                new Video { Title = "Tag Match Video", VideoTags = { new VideoTag { Tag = tag } } },
                new Video { Title = "Tag Alias Video", VideoTags = { new VideoTag { Tag = tag } } },
                new Video { Title = "Unrelated Video" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/api/videos?q={Uri.EscapeDataString(query)}&perPage=10&sort=title&direction=asc", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Contains(items, item => item.GetProperty("title").GetString() == expectedTitle);
    }

    [Theory]
    [InlineData("/api/videos", "title", "Melena Maria Rya Video", "melen")]
    [InlineData("/api/performers", "name", "Melena Maria Rya", "melen")]
    [InlineData("/api/studios", "name", "The Penguin Studio", "peng")]
    [InlineData("/api/tags", "name", "Melena Maria Rya Tag", "melen")]
    [InlineData("/api/groups", "name", "The Penguin Group", "peng")]
    [InlineData("/api/galleries", "title", "The Penguin Gallery", "peng")]
    [InlineData("/api/images", "title", "Melena Maria Rya Image", "melen")]
    [InlineData("/api/audios", "title", "The Penguin Audio", "peng")]
    [InlineData("/api/texts", "title", "Melena Maria Rya Text", "melen")]
    public async Task EntitySearch_WithPartialWordQuery_ReturnsMatchingItems(string endpoint, string propertyName, string expectedValue, string query)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            db.Videos.Add(new Video { Title = "Melena Maria Rya Video" });
            db.Performers.Add(new Performer { Name = "Melena Maria Rya" });
            db.Studios.Add(new Studio { Name = "The Penguin Studio" });
            db.Tags.Add(new Tag { Name = "Melena Maria Rya Tag" });
            db.Groups.Add(new Group { Name = "The Penguin Group" });
            db.Galleries.Add(new Gallery { Title = "The Penguin Gallery" });
            db.Images.Add(new Image { Title = "Melena Maria Rya Image" });
            db.Audios.Add(new Audio { Title = "The Penguin Audio" });
            db.TextDocuments.Add(new TextDocument { Title = "Melena Maria Rya Text" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"{endpoint}?q={Uri.EscapeDataString(query)}&perPage=10", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Contains(items, item => item.GetProperty(propertyName).GetString() == expectedValue);
    }

    [Theory]
    [InlineData("/api/performers", "name", "Alias Performer", "alias-perf")]
    [InlineData("/api/tags", "name", "Alias Tag", "alias-tag")]
    [InlineData("/api/studios", "name", "Alias Studio", "alias-studio")]
    public async Task EntitySearch_WithAliasQuery_ReturnsMatchingItems(string endpoint, string propertyName, string expectedValue, string query)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        await factory.WithDbContextAsync(async db =>
        {
            db.Performers.Add(new Performer { Name = "Alias Performer", Aliases = { new PerformerAlias { Alias = "Alias-Perf Search" } } });
            db.Tags.Add(new Tag { Name = "Alias Tag", Aliases = { new TagAlias { Alias = "Alias-Tag Search" } } });
            db.Studios.Add(new Studio { Name = "Alias Studio", Aliases = { new StudioAlias { Alias = "Alias-Studio Search" } } });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"{endpoint}?q={Uri.EscapeDataString(query)}&perPage=10&sort=name&direction=asc", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Contains(items, item => item.GetProperty(propertyName).GetString() == expectedValue);
    }

    [Fact]
    public async Task TagCreate_WithCustomFields_RoundTripsThroughApi()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        var textFieldResponse = await client.PostAsJsonAsync("/api/custom-fields", new
        {
            key = "source_id",
            label = "Source ID",
            type = "text",
            entityTypes = new[] { CustomFieldEntityTypes.Tag },
            filterable = true,
            sortable = true,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        textFieldResponse.EnsureSuccessStatusCode();

        var dateFieldResponse = await client.PostAsJsonAsync("/api/custom-fields", new
        {
            key = "reviewed_on",
            label = "Reviewed On",
            type = "date",
            entityTypes = new[] { CustomFieldEntityTypes.Tag },
            filterable = true,
            sortable = true,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        dateFieldResponse.EnsureSuccessStatusCode();

        var createResponse = await client.PostAsJsonAsync("/api/tags", new
        {
            name = "Tag with custom fields",
            customFields = new Dictionary<string, object?>
            {
                ["source_id"] = "cf-ui",
                ["reviewed_on"] = "2026-05-09",
            },
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadApiJsonAsync<TagDetailDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.NotNull(created!.CustomFields);
        Assert.Equal("cf-ui", Assert.IsType<JsonElement>(created.CustomFields!["source_id"]).GetString());
        Assert.Equal("2026-05-09", Assert.IsType<JsonElement>(created.CustomFields!["reviewed_on"]).GetString());

        var detailResponse = await client.GetAsync($"/api/tags/{created.Id}", TestContext.Current.CancellationToken);
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadApiJsonAsync<TagDetailDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.NotNull(detail!.CustomFields);
        Assert.Equal("cf-ui", Assert.IsType<JsonElement>(detail.CustomFields!["source_id"]).GetString());
        Assert.Equal("2026-05-09", Assert.IsType<JsonElement>(detail.CustomFields!["reviewed_on"]).GetString());
    }

    [Theory]
    [InlineData(CustomFieldEntityTypes.Video, "/api/videos")]
    [InlineData(CustomFieldEntityTypes.Image, "/api/images")]
    public async Task EntityCustomFields_UpdateAndReload_RoundTripsThroughApi(string entityType, string endpoint)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        var fieldKey = $"audit_marker_{entityType}";
        var fieldResponse = await client.PostAsJsonAsync("/api/custom-fields", new
        {
            key = fieldKey,
            label = "Audit Marker",
            type = "text",
            entityTypes = new[] { entityType },
            filterable = true,
            sortable = true,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        fieldResponse.EnsureSuccessStatusCode();

        var createResponse = await client.PostAsJsonAsync(endpoint, CreateCustomFieldPayload(entityType, fieldKey, "initial"), IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        using var createdPayload = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = createdPayload.RootElement.GetProperty("id").GetInt32();
        Assert.Equal("initial", ReadCustomField(createdPayload.RootElement, fieldKey));

        var updateResponse = await client.PutAsJsonAsync($"{endpoint}/{id}", new
        {
            customFields = new Dictionary<string, object?> { [fieldKey] = "updated" },
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        updateResponse.EnsureSuccessStatusCode();
        using var updatedPayload = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("updated", ReadCustomField(updatedPayload.RootElement, fieldKey));

        var detailResponse = await client.GetAsync($"{endpoint}/{id}", TestContext.Current.CancellationToken);
        detailResponse.EnsureSuccessStatusCode();
        using var detailPayload = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("updated", ReadCustomField(detailPayload.RootElement, fieldKey));

        var clearResponse = await client.PutAsJsonAsync($"{endpoint}/{id}", new
        {
            customFields = new Dictionary<string, object?>(),
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        clearResponse.EnsureSuccessStatusCode();

        var clearedDetailResponse = await client.GetAsync($"{endpoint}/{id}?cacheBust=clear", TestContext.Current.CancellationToken);
        clearedDetailResponse.EnsureSuccessStatusCode();
        using var clearedPayload = JsonDocument.Parse(await clearedDetailResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(ReadCustomField(clearedPayload.RootElement, fieldKey));
    }

    [Fact]
    public async Task TagGroups_And_TagMetadata_RoundTripThroughApi()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        var groupResponse = await client.PostAsJsonAsync("/api/taggroups", new
        {
            name = "Wave Group",
            description = "Wave one grouping",
            color = "#22c55e",
            sortOrder = 7,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        groupResponse.EnsureSuccessStatusCode();
        var group = await groupResponse.Content.ReadApiJsonAsync<TagGroupDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(group);
        Assert.Equal("Wave Group", group!.Name);
        Assert.Equal("#22c55e", group.Color);

        var tagResponse = await client.PostAsJsonAsync("/api/tags", new
        {
            name = "Wave Tag",
            description = "Context aware",
            color = "#0ea5e9",
            tagGroupId = group.Id,
            minOccurrenceSec = 4.5,
            minOccurrencePercent = 12.5,
            aliases = new[] { "wave alias" },
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        tagResponse.EnsureSuccessStatusCode();
        var tag = await tagResponse.Content.ReadApiJsonAsync<TagDetailDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("#0ea5e9", tag!.Color);
        Assert.Equal(group.Id, tag.TagGroupId);
        Assert.Equal("Wave Group", tag.TagGroupName);
        Assert.Equal(4.5, tag.MinOccurrenceSec);
        Assert.Equal(12.5, tag.MinOccurrencePercent);

        var groupsResponse = await client.GetAsync("/api/taggroups", TestContext.Current.CancellationToken);
        groupsResponse.EnsureSuccessStatusCode();
        var groups = await groupsResponse.Content.ReadApiJsonAsync<List<TagGroupDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(groups);
        var listedGroup = Assert.Single(groups!);
        Assert.Equal(1, listedGroup.TagCount);

        var clearResponse = await client.PutAsJsonAsync($"/api/tags/{tag.Id}", new
        {
            name = "Wave Tag",
            color = (string?)null,
            tagGroupId = (int?)null,
            minOccurrenceSec = (double?)null,
            minOccurrencePercent = (double?)null,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        clearResponse.EnsureSuccessStatusCode();
        var cleared = await clearResponse.Content.ReadApiJsonAsync<TagDetailDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.Color);
        Assert.Null(cleared.TagGroupId);
        Assert.Null(cleared.MinOccurrenceSec);
        Assert.Null(cleared.MinOccurrencePercent);
    }

    private static object CreateCustomFieldPayload(string entityType, string fieldKey, string value)
    {
        var customFields = new Dictionary<string, object?> { [fieldKey] = value };
        return entityType switch
        {
            CustomFieldEntityTypes.Video => new { title = "Custom Field Video", organized = false, customFields },
            CustomFieldEntityTypes.Image => new { title = "Custom Field Image", organized = false, customFields },
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, null),
        };
    }

    private static string? ReadCustomField(JsonElement root, string key)
    {
        if (!root.TryGetProperty("customFields", out var customFields)
            || customFields.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || !customFields.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    [Fact]
    public async Task PerformerContextTagApplication_RoundTripsThroughApiAndVideoDetail()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var (videoId, performerId, tagId) = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Context Video", MaxDuration = 100 };
            var performer = new Performer { Name = "Context Performer" };
            var tag = new Tag { Name = "Context Tag", Color = "#f97316" };
            db.AddRange(video, performer, tag);
            await db.SaveChangesAsync();

            db.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = video.Id, PerformerId = performer.Id });
            await db.SaveChangesAsync();
            return (video.Id, performer.Id, tag.Id);
        });

        using var client = factory.CreateAuthenticatedClient();
        var createResponse = await client.PostAsJsonAsync("/api/tagapplications", new
        {
            hostType = "video",
            hostId = videoId,
            contextType = "performer",
            contextId = performerId,
            tagId,
            sourceKey = "user",
            totalDurationSec = 18.0,
            hostDurationSec = 100.0,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var application = await createResponse.Content.ReadApiJsonAsync<TagApplicationDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(application);
        Assert.Equal("performer", application!.ContextType);
        Assert.Equal(performerId, application.ContextId);
        Assert.Equal(18.0, application.TotalDurationSec);

        var videoResponse = await client.GetAsync($"/api/videos/{videoId}", TestContext.Current.CancellationToken);
        videoResponse.EnsureSuccessStatusCode();
        var video = await videoResponse.Content.ReadApiJsonAsync<VideoDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(video);
        var contextApplication = Assert.Single(video!.ContextTagApplications!);
        Assert.Equal(application.Id, contextApplication.Id);
        Assert.Equal("Context Tag", contextApplication.Tag.Name);
        Assert.Equal("#f97316", contextApplication.Tag.Color);

        var invalidResponse = await client.PostAsJsonAsync("/api/tagapplications", new
        {
            hostType = "video",
            hostId = videoId,
            contextType = "performer",
            contextId = performerId + 1000,
            tagId,
        }, IntegrationHttpJson.Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/tagapplications/{application.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await factory.WithDbContextAsync(async db =>
        {
            Assert.Empty(await db.TagApplications.ToListAsync());
        });
    }
}

