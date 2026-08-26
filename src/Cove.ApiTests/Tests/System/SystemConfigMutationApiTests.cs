using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.System;

public sealed class SystemConfigMutationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/system/config")]
    public async Task GivenTaskLocalConfiguration_WhenOwnerUpdatesIt_ThenPermissionsPersistenceAndRestorationAreExact()
    {
        var original = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
        var dataRoot = Path.GetDirectoryName(AsTestFileSystem().GeneratedPath)
            ?? throw new InvalidOperationException("The generated path has no data-root parent.");
        var configPath = Path.Combine(dataRoot, "cove-config.json");
        var configExisted = File.Exists(configPath);
        var priorBytes = configExisted ? await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken) : null;
        var title = $"API test config {Guid.NewGuid():N}";
        var maxConcurrentDownloads = original.MaxConcurrentDownloads == int.MaxValue
            ? original.MaxConcurrentDownloads - 1
            : original.MaxConcurrentDownloads + 1;
        var language = string.Equals(original.Interface.Language, "en-GB", StringComparison.Ordinal)
            ? "en-US"
            : "en-GB";
        var update = original with
        {
            MaxConcurrentDownloads = maxConcurrentDownloads,
            DeleteGeneratedDefault = !original.DeleteGeneratedDefault,
            Interface = original.Interface with { Language = language },
            Ui = original.Ui with { Title = $"  {title}  " },
        };

        try
        {
            Func<Task> forbidden = () => AsUser(ApiTestUsers.Eva).SaveSystemConfigAsync(update);
            await forbidden.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            await AssertConfigFileStateAsync(configPath, configExisted, priorBytes);
            AssertSafeFields(await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken), original);

            var response = await AsUser().SaveSystemConfigAsync(update, TestContext.Current.CancellationToken);
            AssertUpdated(response, title, maxConcurrentDownloads, language, !original.DeleteGeneratedDefault);

            var fresh = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
            AssertUpdated(fresh, title, maxConcurrentDownloads, language, !original.DeleteGeneratedDefault);
            fresh.Scraping.MetadataServers.Select(server => server.Endpoint)
                .Should().Equal(original.Scraping.MetadataServers.Select(server => server.Endpoint));

            File.Exists(configPath).Should().BeTrue();
            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
                await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
                ApiJson.Options);
            persisted.Should().NotBeNull();
            AssertUpdated(
                persisted!,
                title,
                maxConcurrentDownloads,
                language,
                !original.DeleteGeneratedDefault);
        }
        finally
        {
            try
            {
                await AsUser().SaveSystemConfigAsync(original, CancellationToken.None);
                AssertSafeFields(await AsUser().GetSystemConfigAsync(CancellationToken.None), original);
            }
            finally
            {
                if (configExisted)
                    await File.WriteAllBytesAsync(configPath, priorBytes!, CancellationToken.None);
                else if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }
    }

    private static async Task AssertConfigFileStateAsync(
        string configPath,
        bool expectedToExist,
        byte[]? expectedBytes)
    {
        File.Exists(configPath).Should().Be(expectedToExist);
        if (expectedToExist)
            (await File.ReadAllBytesAsync(configPath)).Should().Equal(expectedBytes!);
    }

    private static void AssertUpdated(
        CoveConfigDto config,
        string title,
        int maxConcurrentDownloads,
        string language,
        bool deleteGeneratedDefault)
    {
        config.Ui.Title.Should().Be(title);
        config.MaxConcurrentDownloads.Should().Be(maxConcurrentDownloads);
        config.Interface.Language.Should().Be(language);
        config.DeleteGeneratedDefault.Should().Be(deleteGeneratedDefault);
    }

    private static void AssertSafeFields(CoveConfigDto actual, CoveConfigDto expected)
    {
        actual.Ui.Title.Should().Be(expected.Ui.Title);
        actual.MaxConcurrentDownloads.Should().Be(expected.MaxConcurrentDownloads);
        actual.Interface.Language.Should().Be(expected.Interface.Language);
        actual.DeleteGeneratedDefault.Should().Be(expected.DeleteGeneratedDefault);
        actual.Security.Enabled.Should().Be(expected.Security.Enabled);
        actual.Scraping.MetadataServers.Select(server => server.Endpoint)
            .Should().Equal(expected.Scraping.MetadataServers.Select(server => server.Endpoint));
    }
}
