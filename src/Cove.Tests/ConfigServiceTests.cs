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
}
