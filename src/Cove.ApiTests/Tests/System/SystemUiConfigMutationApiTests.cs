using System.Text.Json;
using AwesomeAssertions.Execution;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane1Collection.Name)]
public sealed class SystemUiConfigMutationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenTaskLocalUiConfiguration_WhenOwnerMergesAndSetsValues_ThenChangesPersistWithoutReplacingOtherValues()
    {
        var original = await AsUser().GetSystemConfigAsync();
        var dataRoot = Path.GetDirectoryName(AsTestFileSystem().GeneratedPath)
            ?? throw new InvalidOperationException("The generated path has no data-root parent.");
        var configPath = Path.Combine(dataRoot, "cove-config.json");
        var configExisted = File.Exists(configPath);
        var priorBytes = configExisted ? await File.ReadAllBytesAsync(configPath) : null;
        var mergedTitle = $"API test UI {Guid.NewGuid():N}";
        var mergedAbbreviation = !original.Ui.AbbreviateCounters;
        var individualAbLoop = !original.Ui.ShowAbLoopControls;

        try
        {
            var mergeInput = new Dictionary<string, object?>
            {
                ["title"] = $"  {mergedTitle}  ",
                ["abbreviateCounters"] = mergedAbbreviation,
            };
            Func<Task> forbiddenMerge = () => AsUser(ApiTestUsers.Eva).ConfigureSystemUiAsync(mergeInput);
            Func<Task> forbiddenSetting = () => AsUser(ApiTestUsers.Eva)
                .ConfigureSystemUiSettingAsync("showAbLoopControls", individualAbLoop);
            await forbiddenMerge.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            await forbiddenSetting.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            await AssertConfigFileStateAsync(configPath, configExisted, priorBytes);
            AssertUiUnchanged(await AsUser().GetSystemConfigAsync(), original);

            var mergedResult = await AsUser().ConfigureSystemUiAsync(mergeInput);
            var afterMerge = await AsUser().GetSystemConfigAsync();
            var settingResult = await AsUser().ConfigureSystemUiSettingAsync(
                "showAbLoopControls",
                individualAbLoop);
            var afterSetting = await AsUser().GetSystemConfigAsync();

            File.Exists(configPath).Should().BeTrue();
            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
                await File.ReadAllTextAsync(configPath),
                ApiJson.Options);
            persisted.Should().NotBeNull();

            using var assertions = new AssertionScope();
            mergedResult.Success.Should().BeTrue();
            afterMerge.Ui.Title.Should().Be(mergedTitle);
            afterMerge.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterMerge.Ui.ShowAbLoopControls.Should().Be(original.Ui.ShowAbLoopControls);
            afterMerge.Ui.AutostartVideo.Should().Be(original.Ui.AutostartVideo);
            afterMerge.Ui.RatingSystemOptions.Should().Be(original.Ui.RatingSystemOptions);

            settingResult.Success.Should().BeTrue();
            settingResult.Key.Should().Be("showAbLoopControls");
            settingResult.Value.ValueKind.Should().Be(
                individualAbLoop ? JsonValueKind.True : JsonValueKind.False);
            settingResult.Value.GetBoolean().Should().Be(individualAbLoop);
            afterSetting.Ui.Title.Should().Be(mergedTitle);
            afterSetting.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterSetting.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
            afterSetting.Ui.AutostartVideo.Should().Be(original.Ui.AutostartVideo);
            afterSetting.Ui.RatingSystemOptions.Should().Be(original.Ui.RatingSystemOptions);

            persisted!.Ui.Title.Should().Be(mergedTitle);
            persisted.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            persisted.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
        }
        finally
        {
            try
            {
                await AsUser().SaveSystemConfigAsync(original);
                AssertUiUnchanged(await AsUser().GetSystemConfigAsync(), original);
            }
            finally
            {
                if (configExisted)
                    await File.WriteAllBytesAsync(configPath, priorBytes!);
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

    private static void AssertUiUnchanged(CoveConfigDto actual, CoveConfigDto expected)
    {
        actual.Ui.Title.Should().Be(expected.Ui.Title);
        actual.Ui.AbbreviateCounters.Should().Be(expected.Ui.AbbreviateCounters);
        actual.Ui.ShowAbLoopControls.Should().Be(expected.Ui.ShowAbLoopControls);
        actual.Ui.AutostartVideo.Should().Be(expected.Ui.AutostartVideo);
        actual.Ui.RatingSystemOptions.Should().Be(expected.Ui.RatingSystemOptions);
    }
}
