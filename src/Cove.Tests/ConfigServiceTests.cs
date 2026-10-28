using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public void MissingSimultaneousJobSetting_DefaultsToOneWithoutChangingParallelTasks()
    {
        var dto = JsonSerializer.Deserialize<CoveConfigDto>("""{"maxParallelTasks":2}""", JsonOptions)!;
        Assert.Equal(1, dto.MaxSimultaneousJobs);
        Assert.Equal(2, dto.MaxParallelTasks);
        Assert.Equal(1, new CoveConfiguration().MaxSimultaneousJobs);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(129, 128)]
    public async Task SimultaneousJobLimit_IsAppliedPersistedAndReadBack(int requested, int expected)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-job-config-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempRoot, "cove-config.json");
        var configuration = new CoveConfiguration { MaxParallelTasks = 2 };
        var service = new ConfigService(configuration, NullLogger<ConfigService>.Instance, configPath);
        try
        {
            await service.UpdateConfigAsync(dto => dto with { MaxSimultaneousJobs = requested });
            Assert.Equal(expected, configuration.MaxSimultaneousJobs);
            Assert.Equal(2, configuration.MaxParallelTasks);
            Assert.Equal(expected, service.GetConfig().MaxSimultaneousJobs);
            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(await File.ReadAllTextAsync(configPath), JsonOptions)!;
            Assert.Equal(expected, persisted.MaxSimultaneousJobs);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UiUpdate_DoesNotReplayStaleNonUiConfiguration()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-config-service-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempRoot, "cove-config.json");
        var configuration = new CoveConfiguration();
        var service = new ConfigService(
            configuration,
            NullLogger<ConfigService>.Instance,
            configPath);
        using var updaterEntered = new ManualResetEventSlim();
        using var continueUpdate = new ManualResetEventSlim();

        try
        {
            var updateTask = Task.Run(() => service.UpdateUiConfigAsync(ui =>
            {
                updaterEntered.Set();
                if (!continueUpdate.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The concurrent configuration mutation was not released.");
                return ui with { Title = "  Concurrent UI title  " };
            }));

            Assert.True(await Task.Run(() => updaterEntered.Wait(TimeSpan.FromSeconds(10))));
            configuration.Auth.Enabled = true;
            continueUpdate.Set();
            await updateTask;

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            };
            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
                await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
                jsonOptions);

            Assert.Equal("Concurrent UI title", configuration.Ui.Title);
            Assert.True(configuration.Auth.Enabled);
            Assert.NotNull(persisted);
            Assert.Equal("Concurrent UI title", persisted.Ui.Title);
            Assert.True(persisted.Security.Enabled);
        }
        finally
        {
            continueUpdate.Set();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UiUpdate_PersistsHiddenScrubberSprites()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-config-service-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempRoot, "cove-config.json");
        var configuration = new CoveConfiguration();
        var service = new ConfigService(
            configuration,
            NullLogger<ConfigService>.Instance,
            configPath);

        try
        {
            Assert.True(configuration.Ui.ShowScrubberSprites);

            await service.UpdateUiConfigAsync(ui => ui with { ShowScrubberSprites = false });

            var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
                await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
                JsonOptions);
            Assert.False(configuration.Ui.ShowScrubberSprites);
            Assert.NotNull(persisted);
            Assert.False(persisted.Ui.ShowScrubberSprites);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SavedConfigWithoutScrubberSpriteSetting_ShowsScrubberSprites()
    {
        // Config files written before the setting existed have no key for it.
        var persisted = JsonSerializer.Deserialize<CoveConfigDto>(
            """{ "ui": { "showAbLoopControls": false } }""",
            JsonOptions);

        Assert.NotNull(persisted);
        Assert.False(persisted.Ui.ShowAbLoopControls);
        Assert.True(persisted.Ui.ShowScrubberSprites);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
