using System.Net;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Extensions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class LegacyPluginsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string ExtensionId = "com.cove.api-test-face-provider";
    private const string DependencyExtensionId = "com.cove.api-test-dependency";
    private const string TaskId = "record-parameters";
    private const string BaselineConfigKey = "apiTestBaseline";
    private const string JobParametersStoreKey = "api-test.job.parameters";
    private const string JobProgressStoreKey = "api-test.job.progress";
    private const string FailInitializationStoreKey = "api-test.initialize.fail";
    private const string CaptureInstallCountParameter = "capture-install-count";
    private const string ExpectedInstallCountParameter = "expected-install-count";
    private const string InstallCountStoreKey = "api-test.install-count";

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
        var originalConfig = await owner.GetLegacyPluginConfigAsync(ExtensionId, TestContext.Current.CancellationToken);
        var originalConfigValues = ToObjectDictionary(originalConfig);
        originalConfig.Should().ContainKey(BaselineConfigKey)
            .WhoseValue.GetString().Should().Be("preserved");

        try
        {
            var memberTasks = await member.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken);
            AssertOnlyApiTestTask(memberTasks);
            AssertApiTestPluginEnabled(await member.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: true);
            AssertConfigurationEquals(await member.GetLegacyPluginConfigAsync(ExtensionId, TestContext.Current.CancellationToken), originalConfig);

            var stateBeforeForbiddenMutations = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);
            var forbiddenConfig = () => member.SetLegacyPluginConfigAsync(ExtensionId, new Dictionary<string, object?> { ["member"] = "blocked" });
            var forbiddenRun = () => member.RunLegacyPluginTaskAsync(new RunPluginTaskDto(ExtensionId, TaskId, new Dictionary<string, string> { ["member"] = "blocked" }));
            var forbiddenSettings = () => member.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = false }));
            var forbiddenReload = () => member.ReloadLegacyPluginsAsync();

            await forbiddenConfig.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenRun.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenSettings.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            await forbiddenReload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            AssertConfigurationEquals(await owner.GetLegacyPluginConfigAsync(ExtensionId, TestContext.Current.CancellationToken), originalConfig);
            (await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(stateBeforeForbiddenMutations);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken));
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: true);

            await owner.SetLegacyPluginConfigAsync(ExtensionId, new Dictionary<string, object?>
            {
                ["theme"] = "midnight",
                ["retries"] = 3,
            }, TestContext.Current.CancellationToken);
            var configured = await owner.GetLegacyPluginConfigAsync(ExtensionId, TestContext.Current.CancellationToken);
            configured["theme"].GetString().Should().Be("midnight");
            configured["retries"].GetInt32().Should().Be(3);

            var started = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string>
                {
                    ["beta"] = "two words",
                    ["alpha"] = "one",
                    [CaptureInstallCountParameter] = "true",
                }), TestContext.Current.CancellationToken);
            var completed = await owner.WaitForTerminalJobAsync(started.JobId, TestContext.Current.CancellationToken);
            completed.Status.Should().Be(JobStatus.Completed);
            completed.Type.Should().Be($"plugin:{ExtensionId}");
            completed.Error.Should().BeNull();
            var stateAfterRun = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);
            stateAfterRun.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"alpha\":\"one\",\"beta\":\"two words\",\"capture-install-count\":\"true\"}");
            stateAfterRun.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");
            var installCount = stateAfterRun.Should().ContainKey(InstallCountStoreKey).WhoseValue;

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = false }), TestContext.Current.CancellationToken);
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: false);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken));
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: true);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken));

            var postEnableRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string>
                {
                    ["phase"] = "after re-enable",
                    [ExpectedInstallCountParameter] = installCount,
                }), TestContext.Current.CancellationToken);
            var postEnableJob = await owner.WaitForTerminalJobAsync(postEnableRun.JobId, TestContext.Current.CancellationToken);
            postEnableJob.Status.Should().Be(JobStatus.Completed);
            postEnableJob.Type.Should().Be($"plugin:{ExtensionId}");
            postEnableJob.Error.Should().BeNull();
            var stateAfterEnable = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);
            stateAfterEnable.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be($"{{\"expected-install-count\":\"{installCount}\",\"phase\":\"after re-enable\"}}");
            stateAfterEnable.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");

            (await owner.ReloadLegacyPluginsAsync(TestContext.Current.CancellationToken)).Message.Should().Be("Plugins reloaded");
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken));
            var postReloadRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "after reload" }), TestContext.Current.CancellationToken);
            var postReloadJob = await owner.WaitForTerminalJobAsync(postReloadRun.JobId, TestContext.Current.CancellationToken);
            postReloadJob.Status.Should().Be(JobStatus.Completed);
            postReloadJob.Type.Should().Be($"plugin:{ExtensionId}");
            postReloadJob.Error.Should().BeNull();
            var stateAfterReload = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);
            stateAfterReload.Should().ContainKey(JobParametersStoreKey)
                .WhoseValue.Should().Be("{\"phase\":\"after reload\"}");
            stateAfterReload.Should().ContainKey(JobProgressStoreKey)
                .WhoseValue.Should().Be("1|API test parameters recorded");

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = false }), TestContext.Current.CancellationToken);
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: false);
            (await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                    ExtensionId,
                    TaskId,
                    new Dictionary<string, string> { ["phase"] = "disabled after reload" }), TestContext.Current.CancellationToken))
                .Should().Be(HttpStatusCode.Conflict);
            (await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(stateAfterReload);

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: true);
            var postReloadEnableRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "re-enabled after reload" }), TestContext.Current.CancellationToken);
            var postReloadEnableJob = await owner.WaitForTerminalJobAsync(postReloadEnableRun.JobId, TestContext.Current.CancellationToken);
            postReloadEnableJob.Status.Should().Be(JobStatus.Completed);
            postReloadEnableJob.Type.Should().Be($"plugin:{ExtensionId}");
            postReloadEnableJob.Error.Should().BeNull();
            var finalState = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);
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
                await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.ReloadLegacyPluginsAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.SetLegacyPluginConfigAsync(ExtensionId, originalConfigValues, TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                AssertConfigurationEquals(await owner.GetLegacyPluginConfigAsync(ExtensionId, TestContext.Current.CancellationToken), originalConfig);
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
        var stateBeforeDisable = await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken);

        try
        {
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = false }), TestContext.Current.CancellationToken);
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: false);
            AssertOnlyApiTestTask(await owner.GetLegacyPluginTasksAsync(TestContext.Current.CancellationToken));

            var status = await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["disabled"] = "blocked" }), TestContext.Current.CancellationToken);

            status.Should().Be(HttpStatusCode.Conflict);
            (await owner.GetExtensionDataAsync(ExtensionId, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(stateBeforeDisable);
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                    new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            try
            {
                await owner.ReloadLegacyPluginsAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }

            if (cleanupErrors.Count > 0)
                throw new AggregateException("Disabled legacy plugin test cleanup failed.", cleanupErrors);
        }
    }

    [Fact]
    public async Task GivenDisabledJobExtension_WhenReinitializationFails_ThenItRemainsDisabledAndNoTaskCanRun()
    {
        var owner = AsUser();

        try
        {
            await owner.SetExtensionDataAsync(ExtensionId, FailInitializationStoreKey, "true", TestContext.Current.CancellationToken);
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = false }), TestContext.Current.CancellationToken);

            var reEnable = () => owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = true }));

            await reEnable.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 409 (Conflict)*");
            AssertApiTestPluginEnabled(await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken), expected: false);
            (await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                    ExtensionId,
                    TaskId,
                    new Dictionary<string, string> { ["phase"] = "failed initialization" }), TestContext.Current.CancellationToken))
                .Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await owner.SetExtensionDataAsync(ExtensionId, FailInitializationStoreKey, "false", TestContext.Current.CancellationToken);
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);
            await owner.ReloadLegacyPluginsAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GivenDependentLegacyPlugins_WhenEitherStateChanges_ThenTheWholeClosureIsReportedConsistently()
    {
        var owner = AsUser();

        try
        {
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [DependencyExtensionId] = false }), TestContext.Current.CancellationToken);

            var disabledPlugins = await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken);
            AssertPluginEnabled(disabledPlugins, ExtensionId, expected: false);
            AssertPluginEnabled(disabledPlugins, DependencyExtensionId, expected: false);
            (await owner.TryRunLegacyPluginTaskAsync(new RunPluginTaskDto(
                    ExtensionId,
                    TaskId,
                    new Dictionary<string, string> { ["phase"] = "dependency disabled" }), TestContext.Current.CancellationToken))
                .Should().Be(HttpStatusCode.Conflict);

            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool> { [ExtensionId] = true }), TestContext.Current.CancellationToken);

            var reEnabledPlugins = await owner.GetLegacyPluginsAsync(TestContext.Current.CancellationToken);
            AssertPluginEnabled(reEnabledPlugins, ExtensionId, expected: true);
            AssertPluginEnabled(reEnabledPlugins, DependencyExtensionId, expected: true);
            var reEnabledRun = await owner.RunLegacyPluginTaskAsync(new RunPluginTaskDto(
                ExtensionId,
                TaskId,
                new Dictionary<string, string> { ["phase"] = "dependency re-enabled" }), TestContext.Current.CancellationToken);
            (await owner.WaitForTerminalJobAsync(reEnabledRun.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await owner.UpdateLegacyPluginSettingsAsync(new PluginSettingsDto(
                new Dictionary<string, bool>
                {
                    [DependencyExtensionId] = true,
                    [ExtensionId] = true,
                }), TestContext.Current.CancellationToken);
            await owner.ReloadLegacyPluginsAsync(TestContext.Current.CancellationToken);
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

    private static void AssertPluginEnabled(
        IReadOnlyList<PluginDto> plugins,
        string extensionId,
        bool expected)
        => plugins.Should().ContainSingle(item => item.Id == extensionId).Which.Enabled.Should().Be(expected);
}
