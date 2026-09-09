using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

public sealed class EntityUpdatedAtSmokeTests
{
    private static readonly DateTime OldTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("videos")]
    [InlineData("audios")]
    [InlineData("texts")]
    [InlineData("performers")]
    [InlineData("studios")]
    [InlineData("tags")]
    [InlineData("images")]
    [InlineData("galleries")]
    [InlineData("groups")]
    public async Task RelationshipOnlyUpdate_TouchesParent_AndIdenticalRepeatDoesNot(string kind)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        var id = await SeedAsync(factory, kind);
        using var client = factory.CreateAuthenticatedClient();
        var payload = RelationshipPayload(kind);

        var update = await client.PutAsJsonAsync($"/api/{kind}/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        update.EnsureSuccessStatusCode();
        var changedAt = await ReadUpdatedAtAsync(factory, kind, id);
        Assert.True(changedAt > OldTimestamp);

        var list = await client.GetAsync($"/api/{kind}?sort=updated_at&direction=desc&perPage=10", TestContext.Current.CancellationToken);
        list.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
            Assert.Equal(id, json.RootElement.GetProperty("items")[0].GetProperty("id").GetInt32());

        var repeat = await client.PutAsJsonAsync($"/api/{kind}/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        repeat.EnsureSuccessStatusCode();
        Assert.Equal(changedAt, await ReadUpdatedAtAsync(factory, kind, id));
    }

    [Theory]
    [InlineData("videos", "video")]
    [InlineData("audios", "audio")]
    [InlineData("texts", "text")]
    [InlineData("performers", "performer")]
    [InlineData("studios", "studio")]
    [InlineData("tags", "tag")]
    [InlineData("images", "image")]
    [InlineData("galleries", "gallery")]
    [InlineData("groups", "group")]
    public async Task CustomFieldOnlyUpdate_TouchesParent_AndIdenticalRepeatDoesNot(string kind, string entityType)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();
        var definition = await client.PostAsJsonAsync("/api/custom-fields", new
        {
            key = "timestamp_marker",
            label = "Timestamp marker",
            type = "text",
            entityTypes = new[] { entityType },
        }, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        definition.EnsureSuccessStatusCode();
        var id = await SeedAsync(factory, kind);
        var payload = new { customFields = new Dictionary<string, object?> { ["timestamp_marker"] = "changed" } };

        var update = await client.PutAsJsonAsync($"/api/{kind}/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        update.EnsureSuccessStatusCode();
        var changedAt = await ReadUpdatedAtAsync(factory, kind, id);
        Assert.True(changedAt > OldTimestamp);

        var repeat = await client.PutAsJsonAsync($"/api/{kind}/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        repeat.EnsureSuccessStatusCode();
        Assert.Equal(changedAt, await ReadUpdatedAtAsync(factory, kind, id));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("number")]
    [InlineData("timestamp")]
    public async Task StorageNormalizedCustomFieldRepeat_DoesNotTouchParent(string fieldType)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();
        var definition = await client.PostAsJsonAsync("/api/custom-fields", new
        {
            key = "timestamp_storage_value",
            label = "Timestamp storage value",
            type = fieldType,
            entityTypes = new[] { "video" },
        }, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        definition.EnsureSuccessStatusCode();

        var id = await SeedAsync(factory, "videos");
        using var json = JsonDocument.Parse("{\"value\":100000000000000000000000000000}");
        object value = fieldType switch
        {
            "json" => json.RootElement.Clone(),
            "number" => 1.123456789m,
            "timestamp" => "2026-08-28T03:04:05.1234567Z",
            _ => throw new ArgumentOutOfRangeException(nameof(fieldType)),
        };
        var payload = new { customFields = new Dictionary<string, object?> { ["timestamp_storage_value"] = value } };

        var update = await client.PutAsJsonAsync($"/api/videos/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        update.EnsureSuccessStatusCode();
        var changedAt = await ReadUpdatedAtAsync(factory, "videos", id);

        var repeat = await client.PutAsJsonAsync($"/api/videos/{id}", payload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
        repeat.EnsureSuccessStatusCode();
        Assert.Equal(changedAt, await ReadUpdatedAtAsync(factory, "videos", id));

        if (fieldType == "json")
        {
            using var changedJson = JsonDocument.Parse("{\"value\":100000000000000000000000000001}");
            var changedPayload = new { customFields = new Dictionary<string, object?> { ["timestamp_storage_value"] = changedJson.RootElement.Clone() } };
            var changed = await client.PutAsJsonAsync($"/api/videos/{id}", changedPayload, IntegrationHttpJson.Options, TestContext.Current.CancellationToken);
            changed.EnsureSuccessStatusCode();
            Assert.True(await ReadUpdatedAtAsync(factory, "videos", id) > changedAt);
        }
    }

    private static object RelationshipPayload(string kind) => kind switch
    {
        "performers" => new { aliases = new[] { "timestamp-alias" } },
        "tags" => new { aliases = new[] { "timestamp-alias" } },
        _ => new { urls = new[] { "https://example.invalid/timestamp" } },
    };

    private static Task<int> SeedAsync(CoveWebApplicationFactory factory, string kind) =>
        factory.WithDbContextAsync(async db =>
        {
            BaseEntity target = kind switch
            {
                "videos" => new Video { Title = "Timestamp target" },
                "audios" => new Audio { Title = "Timestamp target" },
                "texts" => new TextDocument { Title = "Timestamp target" },
                "performers" => new Performer { Name = "Timestamp target" },
                "studios" => new Studio { Name = "Timestamp target" },
                "tags" => new Tag { Name = "Timestamp target" },
                "images" => new Image { Title = "Timestamp target" },
                "galleries" => new Gallery { Title = "Timestamp target" },
                "groups" => new Group { Name = "Timestamp target" },
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            BaseEntity competitor = kind switch
            {
                "videos" => new Video { Title = "Timestamp competitor" },
                "audios" => new Audio { Title = "Timestamp competitor" },
                "texts" => new TextDocument { Title = "Timestamp competitor" },
                "performers" => new Performer { Name = "Timestamp competitor" },
                "studios" => new Studio { Name = "Timestamp competitor" },
                "tags" => new Tag { Name = "Timestamp competitor" },
                "images" => new Image { Title = "Timestamp competitor" },
                "galleries" => new Gallery { Title = "Timestamp competitor" },
                "groups" => new Group { Name = "Timestamp competitor" },
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            db.AddRange(target, competitor);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            target.UpdatedAt = OldTimestamp;
            competitor.UpdatedAt = OldTimestamp.AddYears(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await ResetTimestampAsync(db, kind, target.Id);
            return target.Id;
        });

    private static async Task ResetTimestampAsync(Cove.Data.CoveContext db, string kind, int id)
    {
        var ct = TestContext.Current.CancellationToken;
        _ = kind switch
        {
            "videos" => await db.Videos.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "audios" => await db.Audios.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "texts" => await db.TextDocuments.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "performers" => await db.Performers.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "studios" => await db.Studios.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "tags" => await db.Tags.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "images" => await db.Images.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "galleries" => await db.Galleries.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            "groups" => await db.Groups.Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, OldTimestamp), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static Task<DateTime> ReadUpdatedAtAsync(CoveWebApplicationFactory factory, string kind, int id) =>
        factory.WithDbContextAsync(async db => kind switch
        {
            "videos" => await db.Videos.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "audios" => await db.Audios.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "texts" => await db.TextDocuments.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "performers" => await db.Performers.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "studios" => await db.Studios.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "tags" => await db.Tags.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "images" => await db.Images.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "galleries" => await db.Galleries.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            "groups" => await db.Groups.Where(x => x.Id == id).Select(x => x.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

}
