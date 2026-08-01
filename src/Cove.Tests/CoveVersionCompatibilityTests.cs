using Cove.Core.Common;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public class CoveVersionCompatibilityTests
{
    [Theory]
    [InlineData("1.1.0", "1.1.0", true)]
    [InlineData("1.1.0-dev.1", "1.1.0", true)]
    [InlineData("1.1.0-dev.2", "1.1.0-dev.2", true)]
    [InlineData("1.1.0-dev.1", "1.1.0-dev.2", false)]
    [InlineData("1.1.0", "1.1.0-dev.1", false)]
    [InlineData("1.1.1", "1.1.0-dev.200", true)]
    [InlineData("1.2.0", "1.1.0-dev.200", true)]
    [InlineData("2.0.0", "1.9.9-dev.200", true)]
    [InlineData("v1.1.0-dev.2+9a03e2ef", "1.1.0-dev.2", true)]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.1", true)]
    [InlineData("1.2.0-rc.2", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0-rc.2", true)]
    public void IsAtLeast_orders_development_builds_after_their_base_release(
        string current,
        string minimum,
        bool expected)
    {
        Assert.Equal(expected, CoveVersionCompatibility.IsAtLeast(current, minimum));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.1.0-dev.nope")]
    [InlineData("1.1.0-dev.1.2")]
    public void TryParse_rejects_invalid_versions(string value)
    {
        Assert.False(CoveVersionCompatibility.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1.1.0", false)]
    [InlineData("1.1.0-dev.41", false)]
    [InlineData("1.1.0-dev.42", true)]
    [InlineData("1.1.0-dev.43", true)]
    [InlineData("1.1.1", true)]
    public void Extension_dependency_validation_uses_the_development_build_floor(
        string runningVersion,
        bool compatible)
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = runningVersion,
        });
        manager.Register(new DevelopmentBuildExtension());

        var problems = manager.ValidateDependencies();

        Assert.Equal(compatible, problems.Count == 0);
    }

    private sealed class DevelopmentBuildExtension : IExtension
    {
        public string Id => "test.development-build";
        public string Name => "Development build test";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public string? MinCoveVersion => "1.1.0-dev.42";

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }
}
