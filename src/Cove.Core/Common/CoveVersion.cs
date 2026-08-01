using System.Reflection;

namespace Cove.Core.Common;

/// <summary>
/// Single runtime source of truth for the Cove version. The value is baked into the
/// assembly at build time from the git tag (see Directory.Build.targets), so a tagged
/// release (e.g. v0.0.36) reports "0.0.36" and local/dev builds report
/// "&lt;tag&gt;-dev.&lt;commit distance&gt;".
/// </summary>
public static class CoveVersion
{
    private static readonly Lazy<(string Display, string Numeric)> Resolved = new(Resolve);

    /// <summary>
    /// Full version string for display, including any prerelease suffix (e.g. "0.0.35-dev").
    /// Shown on the About / Runtime Status pages.
    /// </summary>
    public static string Display => Resolved.Value.Display;

    /// <summary>
    /// Numeric major.minor.patch assembly version (e.g. "0.0.35"). Never carries a
    /// prerelease suffix. Host compatibility checks must use <see cref="Display"/> so
    /// development-build floors remain distinguishable from their base release.
    /// </summary>
    public static string Numeric => Resolved.Value.Numeric;

    private static (string Display, string Numeric) Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CoveVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        // Drop any build metadata (e.g. the "+<sha>" SourceLink can append).
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            if (plusIndex >= 0)
                informational = informational[..plusIndex];
        }

        var display = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational.Trim();

        // Numeric portion only (strip prerelease suffix) for semver comparisons.
        var numeric = display;
        var dashIndex = numeric.IndexOf('-');
        if (dashIndex >= 0)
            numeric = numeric[..dashIndex];

        if (string.IsNullOrWhiteSpace(numeric))
            numeric = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        return (display, numeric);
    }
}
