using System.Text.Json;
using AwesomeAssertions.Execution;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane1Collection.Name)]
public sealed class SystemUiConfigMutationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/system/config/ui")]
    [CoversEndpoint("PUT", "/api/system/config/ui/{key}")]
    public async Task GivenTaskLocalUiConfiguration_WhenOwnerMergesAndSetsValues_ThenChangesPersistWithoutReplacingOtherValues()
    {
        var original = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
        var dataRoot = Path.GetDirectoryName(AsTestFileSystem().GeneratedPath)
            ?? throw new InvalidOperationException("The generated path has no data-root parent.");
        var configPath = Path.Combine(dataRoot, "cove-config.json");
        var configExisted = File.Exists(configPath);
        var priorBytes = configExisted ? await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken) : null;
        var mergedTitle = $"API test UI {Guid.NewGuid():N}";
        var mergedAbbreviation = !original.Ui.AbbreviateCounters;
        var individualAbLoop = !original.Ui.ShowAbLoopControls;
        var mergePrecision = original.Ui.RatingSystemOptions.StarPrecision == RatingStarPrecision.Half
            ? RatingStarPrecision.Quarter
            : RatingStarPrecision.Half;
        var nestedPrecision = mergePrecision == RatingStarPrecision.Tenth
            ? RatingStarPrecision.Quarter
            : RatingStarPrecision.Tenth;
        var keySuffix = Guid.NewGuid().ToString("N");
        var mergedBindingKey = $"apiTest.merge.{keySuffix}";
        var mergedBindingValue = "Ctrl+Shift+M";
        var dottedBindingKey = $"apiTest.open.{keySuffix}";
        var dottedBindingValue = "Ctrl+Shift+O";
        var expectedMergedBindings = new Dictionary<string, string>(
            original.Ui.KeybindingOverrides,
            StringComparer.OrdinalIgnoreCase)
        {
            [mergedBindingKey] = mergedBindingValue,
        };
        var expectedOpenBindings = new Dictionary<string, string>(
            expectedMergedBindings,
            StringComparer.OrdinalIgnoreCase)
        {
            [dottedBindingKey] = dottedBindingValue,
        };

        try
        {
            var mergeInput = new Dictionary<string, object?>
            {
                ["title"] = $"  {mergedTitle}  ",
                ["abbreviateCounters"] = mergedAbbreviation,
                ["ratingSystemOptions"] = new Dictionary<string, object?>
                {
                    ["starPrecision"] = mergePrecision,
                },
                ["keybindingOverrides"] = new Dictionary<string, string>
                {
                    [mergedBindingKey] = mergedBindingValue,
                },
            };
            Func<Task> forbiddenMerge = () => AsUser(ApiTestUsers.Eva).ConfigureSystemUiAsync(mergeInput);
            Func<Task> forbiddenSetting = () => AsUser(ApiTestUsers.Eva)
                .ConfigureSystemUiSettingAsync("showAbLoopControls", individualAbLoop);
            await forbiddenMerge.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            await forbiddenSetting.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            await AssertConfigFileStateAsync(configPath, configExisted, priorBytes);
            AssertUiUnchanged(await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken), original);

            var mergedResult = await AsUser().ConfigureSystemUiAsync(mergeInput, TestContext.Current.CancellationToken);
            var afterMerge = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
            var settingResult = await AsUser().ConfigureSystemUiSettingAsync("showAbLoopControls", individualAbLoop, TestContext.Current.CancellationToken);
            var afterSetting = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
            var nestedResult = await AsUser().ConfigureSystemUiSettingAsync("RatingSystemOptions.StarPrecision", nestedPrecision, TestContext.Current.CancellationToken);
            var afterNested = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);
            var openDictionaryResult = await AsUser().ConfigureSystemUiSettingAsync($"keybindingOverrides.{dottedBindingKey}", dottedBindingValue, TestContext.Current.CancellationToken);
            var afterOpenDictionary = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);

            var beforeInvalidBytes = await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken);
            async Task AssertInvalidAsync(Func<Task> request)
            {
                await request.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*returned 400 (BadRequest)*");
                (await File.ReadAllBytesAsync(configPath)).Should().Equal(beforeInvalidBytes);
                AssertUiUnchanged(await AsUser().GetSystemConfigAsync(), afterOpenDictionary);
            }

            await AssertInvalidAsync(() => AsUser().ConfigureSystemUiAsync(
                new Dictionary<string, object?>
                {
                    ["title"] = "must not persist",
                    ["notAUiSetting"] = true,
                }));
            await AssertInvalidAsync(() => AsUser().ConfigureSystemUiAsync(
                new Dictionary<string, object?>
                {
                    ["title"] = "must not persist",
                    ["ratingSystemOptions"] = new Dictionary<string, object?>
                    {
                        ["notASetting"] = true,
                    },
                }));
            await AssertInvalidAsync(() => AsUser().ConfigureSystemUiSettingAsync(
                "ratingSystemOptions",
                new Dictionary<string, object?>
                {
                    ["type"] = original.Ui.RatingSystemOptions.Type,
                    ["starPrecision"] = nestedPrecision,
                    ["notASetting"] = true,
                }));
            await AssertInvalidAsync(() => AsUser().ConfigureSystemUiAsync(
                new Dictionary<string, object?> { ["ratingSystemOptions"] = null }));
            await AssertInvalidAsync(() => AsUser().ConfigureSystemUiAsync(
                new Dictionary<string, object?> { ["keybindingOverrides"] = null }));
            await AssertInvalidAsync(() => AsUser()
                .ConfigureSystemUiSettingAsync("ratingSystemOptions.starPrecision", 999));
            var duplicateBindings = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [$"duplicate.{keySuffix}"] = "Ctrl+1",
                [$"DUPLICATE.{keySuffix}"] = "Ctrl+2",
            };
            await AssertInvalidAsync(() => AsUser()
                .ConfigureSystemUiSettingAsync("keybindingOverrides", duplicateBindings));
            await AssertInvalidAsync(() => AsUser()
                .ConfigureSystemUiSettingAsync("showAbLoopControls", "not-a-boolean"));
            var afterInvalid = await AsUser().GetSystemConfigAsync(TestContext.Current.CancellationToken);

            File.Exists(configPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken)).Should().Equal(beforeInvalidBytes);
            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
                await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
                ApiJson.Options);
            persisted.Should().NotBeNull();

            using var assertions = new AssertionScope();
            mergedResult.Success.Should().BeTrue();
            afterMerge.Ui.Title.Should().Be(mergedTitle);
            afterMerge.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterMerge.Ui.ShowAbLoopControls.Should().Be(original.Ui.ShowAbLoopControls);
            afterMerge.Ui.AutostartVideo.Should().Be(original.Ui.AutostartVideo);
            afterMerge.Ui.RatingSystemOptions.Type.Should().Be(original.Ui.RatingSystemOptions.Type);
            afterMerge.Ui.RatingSystemOptions.StarPrecision.Should().Be(mergePrecision);
            afterMerge.Ui.KeybindingOverrides.Should().BeEquivalentTo(expectedMergedBindings);

            settingResult.Success.Should().BeTrue();
            settingResult.Key.Should().Be("showAbLoopControls");
            settingResult.Value.ValueKind.Should().Be(
                individualAbLoop ? JsonValueKind.True : JsonValueKind.False);
            settingResult.Value.GetBoolean().Should().Be(individualAbLoop);
            afterSetting.Ui.Title.Should().Be(mergedTitle);
            afterSetting.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterSetting.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
            afterSetting.Ui.AutostartVideo.Should().Be(original.Ui.AutostartVideo);
            afterSetting.Ui.RatingSystemOptions.Should().Be(afterMerge.Ui.RatingSystemOptions);
            afterSetting.Ui.KeybindingOverrides.Should().BeEquivalentTo(
                afterMerge.Ui.KeybindingOverrides);

            nestedResult.Success.Should().BeTrue();
            nestedResult.Key.Should().Be("RatingSystemOptions.StarPrecision");
            nestedResult.Value.GetString().Should().Be(nestedPrecision.ToString().ToLowerInvariant());
            afterNested.Ui.Title.Should().Be(mergedTitle);
            afterNested.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterNested.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
            afterNested.Ui.RatingSystemOptions.Type.Should().Be(original.Ui.RatingSystemOptions.Type);
            afterNested.Ui.RatingSystemOptions.StarPrecision.Should().Be(nestedPrecision);
            afterNested.Ui.KeybindingOverrides.Should().BeEquivalentTo(
                afterMerge.Ui.KeybindingOverrides);

            openDictionaryResult.Success.Should().BeTrue();
            openDictionaryResult.Key.Should().Be($"keybindingOverrides.{dottedBindingKey}");
            openDictionaryResult.Value.GetString().Should().Be(dottedBindingValue);
            afterOpenDictionary.Ui.Title.Should().Be(mergedTitle);
            afterOpenDictionary.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            afterOpenDictionary.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
            afterOpenDictionary.Ui.RatingSystemOptions.Should().Be(afterNested.Ui.RatingSystemOptions);
            afterOpenDictionary.Ui.KeybindingOverrides.Should().BeEquivalentTo(expectedOpenBindings);

            AssertUiUnchanged(afterInvalid, afterOpenDictionary);

            persisted!.Ui.Title.Should().Be(mergedTitle);
            persisted.Ui.AbbreviateCounters.Should().Be(mergedAbbreviation);
            persisted.Ui.ShowAbLoopControls.Should().Be(individualAbLoop);
            persisted.Ui.RatingSystemOptions.Should().Be(afterOpenDictionary.Ui.RatingSystemOptions);
            persisted.Ui.KeybindingOverrides.Should().BeEquivalentTo(
                afterOpenDictionary.Ui.KeybindingOverrides);
        }
        finally
        {
            try
            {
                await AsUser().SaveSystemConfigAsync(original, CancellationToken.None);
                AssertUiUnchanged(await AsUser().GetSystemConfigAsync(CancellationToken.None), original);
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

    private static void AssertUiUnchanged(CoveConfigDto actual, CoveConfigDto expected)
    {
        actual.Ui.Title.Should().Be(expected.Ui.Title);
        actual.Ui.AbbreviateCounters.Should().Be(expected.Ui.AbbreviateCounters);
        actual.Ui.ShowAbLoopControls.Should().Be(expected.Ui.ShowAbLoopControls);
        actual.Ui.AutostartVideo.Should().Be(expected.Ui.AutostartVideo);
        actual.Ui.RatingSystemOptions.Should().Be(expected.Ui.RatingSystemOptions);
        actual.Ui.KeybindingOverrides.Should().BeEquivalentTo(expected.Ui.KeybindingOverrides);
    }
}
