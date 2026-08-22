using System.Net;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Extensions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class LegacyPluginsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string ExtensionId = "com.cove.api-test-face-provider";
    private const string TaskId = "record-parameters";
    private const string BaselineConfigKey = "apiTestBaseline";
    private const string JobParametersStoreKey = "api-test.job.parameters";
    private const string JobProgressStoreKey = "api-test.job.progress";

    [Fact]
    [CoversEndpoint("GET", "/api/plugins/tasks")]
    [CoversEndpoint("GET", "/api/plugins/{pluginid}/config")]
    [CoversEndpoint("POST", "/api/plugins/run-task")]
    [CoversEndpoint("POST", "/api/plugins/settings")]
    [CoversEndpoint("POST", "/api/plugins/{pluginid}/config")]
    [CoversEndpoint("POST", "/api/plugins/reload")]
    public async Task GivenInstalledJobExtension_WhenLegacyPluginAdministrationChangesState_ThenPermissionsTasksAndConfigurationRemainExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var originalConfig = await owner.GetLegacyPluginConfigAsync(ExtensionId);
        var originalConfigValues = ToObjectDictionary(originalConfig);
        originalConfig.Should().ContainKey(BaselineConfigKey)
            .WhoseValue.GetString().Should().Be("preserved");

        try
        {
            var memberTasks = await member.GetLegacyPluginTasksAsync();
            AssertOnlyApiTestTask(memberTasks);
            AssertApiTestPluginEnabled(await member.GetLegacyPluginsAsync(), expected: true);
            AssertConfigurationEquals(await member.GetLegacyPluginConfigAsync(ExtensionId), originalConfig);

            var stateBeforeForbiddenMutations = await owner.GetExtensionDataAsync(ExtensionId);
            var forbiddenConfig = () => member.SetLegacyPluginConfigAsync(ExtensionId, new Dictionary<string, object?> { ["member"] = "blocked" });
            var forbiddenRun = () => member.RunLegacyPluginTaskAsync(new RunPluginTaskDto(ExtensionId, TaskId, new Dictionary<string, string> { ["member"] = "blocked" }));
            var forbiddenSettings = () => member.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = false }));
            var forbiddenReload = () => member.ReloadLegacyPluginsAsync();

            await forbiddenConfig.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenRun.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenSettings.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenReload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            AssertConfigurationEquals(await owner.GetLegacyPluginConfigAsync(ExtensionId), originalConfig);
            (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(stateBeforeForbiddenMutations);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync());
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: true);

            await owner.SetLegacyPluginConfigAsync(ExtensionId, new Dictionary<string, object?>
            {
                ["theme"] = "midnight",
                ["retries"] = 3,
            });
            var configured = await owner.GetLegacyPluginConfigAsync(ExtensionId);
            configured["theme"].GetString().Should().Be("midnight");
            configured["retries"].GetInt32().Should().Be(3);

            var started = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string>
                {
                    ["beta"] = "two words",
                    ["alpha"] = "one",
                }));
            var completed = await owner.WaitForTerminalJobAsync(started.JobId);
            completed.Status.Should().Be(JobStatus.Completed);
            completed.Type.Should().Be($"plugin:{ExtensionId}");
            completed.Error.Should().BeNull();
            var stateAfterRun = await owner.GetExtensionDataAsync(ExtensionId);
            stateAfterRun.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"alpha\":\"one\",\"beta\":\"two words\"}");
            stateAfterRun.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = false }));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: false);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync());
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = true }));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: true);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync());

            var postEnableRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "after re-enable" }));
            var postEnableJob = await owner.WaitForTerminalJobAsync(postEnableRun.JobId);
            postEnableJob.Status.Should().Be(JobStatus.Completed);
            postEnableJob.Type.Should().Be($"plugin:{ExtensionId}");
            postEnableJob.Error.Should().BeNull();
            var stateAfterEnable = await owner.GetExtensionDataAsync(ExtensionId);
            stateAfterEnable.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"phase\":\"after re-enable\"}");
            stateAfterEnable.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");

            (await owner.ReloadLegacyPluginsAsync()).Message.Should().Be("Plugins reloaded");
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync());
            var postReloadRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "after reload" }));
            var postReloadJob = await owner.WaitForTerminalJobAsync(postReloadRun.JobId);
            postReloadJob.Status.Should().Be(JobStatus.Completed);
            postReloadJob.Type.Should().Be($"plugin:{ExtensionId}");
            postReloadJob.Error.Should().BeNull();
            var stateAfterReload = await owner.GetExtensionDataAsync(ExtensionId);
            stateAfterReload.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"phase\":\"after reload\"}");
            stateAfterReload.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = false }));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: false);
            (await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                    ExtensionId,
                    TaskId,
                    new Dictionary<string, string> { ["phase"] = "disabled after reload" })))
                .Should().Be(HttpStatusCode.Conflict);
            (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(stateAfterReload);

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = true }));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: true);
            var postReloadEnableRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "re-enabled after reload" }));
            var postReloadEnableJob = await owner.WaitForTerminalJobAsync(postReloadEnableRun.JobId);
            postReloadEnableJob.Status.Should().Be(JobStatus.Completed);
            postReloadEnableJob.Type.Should().Be($"plugin:{ExtensionId}");
            postReloadEnableJob.Error.Should().BeNull();
            var finalState = await owner.GetExtensionDataAsync(ExtensionId);
            finalState.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"phase\":\"re-enabled after reload\"}");
            finalState.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = true }));
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.ReloadLegacyPluginsAsync();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.SetLegacyPluginConfigAsync(ExtensionId, originalConfigValues);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                AssertConfigurationEquals(await owner.GetLegacyPluginConfigAsync(ExtensionId), originalConfig);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            if (cleanupErrors.Count > 0)
                throw new AggregateException("Legacy plugin test cleanup failed.", cleanupErrors);
        }
    }

    [Fact]
    public async Task GivenDisabledJobExtension_WhenLegacyTaskIsRun_ThenRequestIsRejectedWithoutStateChanges()
    {
        var owner = AsUser();
        var stateBeforeDisable = await owner.GetExtensionDataAsync(ExtensionId);

        try
        {
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = false }));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(), expected: false);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync());

            var status = await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["disabled"] = "blocked" }));

            status.Should().Be(HttpStatusCode.Conflict);
            (await owner.GetExtensionDataAsync(ExtensionId)).Should().BeEquivalentTo(stateBeforeDisable);
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                    new Dictionary<string, bool> { [ExtensionId] = true }));
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.ReloadLegacyPluginsAsync();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            if (cleanupErrors.Count > 0)
                throw new AggregateException("Disabled legacy plugin test cleanup failed.", cleanupErrors);
        }
    }

    private static Dictionary<string, object?> ToObjectDictionary(Dictionary<string, JsonElement> values)
        => values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Clone());

    private static void AssertConfigurationEquals(
        IReadOnlyDictionary<string, JsonElement> actual,
        IReadOnlyDictionary<string, JsonElement> expected)
    {
        actual.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (key, expectedValue) in expected)
        {
            actual.Should().ContainKey(key);
            JsonElement.DeepEquals(actual[key], expectedValue).Should().BeTrue(
                because: $"plugin configuration value '{key}' should be restored exactly");
        }
    }

    private static void AssertOnlyApiTestTask(IReadOnlyList<PluginTaskDto> tasks)
    {
        tasks.Should().ContainSingle();
        tasks.Single().Name.Should().Be(TaskId);
        tasks.Single().Description.Should().Be("Record API test parameters");
    }

    private static void AssertApiTestPluginEnabled(IReadOnlyList<PluginDto> plugins, bool expected)
    {
        var plugin = plugins.Should().ContainSingle(item => item.Id == ExtensionId).Which;
        plugin.Enabled.Should().Be(expected);
        plugin.Tasks.Should().ContainSingle();
        plugin.Tasks.Single().Name.Should().Be(TaskId);
        plugin.Tasks.Single().Description.Should().Be("Record API test parameters");
    }
}
