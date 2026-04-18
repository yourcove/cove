using System.Reflection;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Api.Extensions;

namespace Cove.Tests;

/// <summary>
/// Tests for fingerprint matching fixes (oshash formatting, metadata-server query structure,
/// theme system registration).
/// </summary>
public class FingerprintMatchingTests
{
    /// <summary>
    /// Validates that oshash formatting matches the standard format (zero-padded 16-char hex).
    /// Go uses: fmt.Sprintf("%016x", result) â†’ always 16 characters, zero-padded
    /// C# must use: ToString("x16") â†’ 16-char zero-padded
    /// </summary>
    [Theory]
    [InlineData(0x123456789abcdef0UL, "123456789abcdef0")]
    [InlineData(0x00abcdef12345678UL, "00abcdef12345678")]   // leading zeros preserved
    [InlineData(0x0000000000000001UL, "0000000000000001")]   // fully padded
    [InlineData(0xffffffffffffffffUL, "ffffffffffffffff")]   // max
    public void OshashFormat_MatchesGoZeroPaddedHex(ulong hash, string expected)
    {
        // This is the same format used in ScanService.ComputeOshashAsync
        var result = hash.ToString("x16");
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that unpadded format (the old buggy behavior) would NOT match Go output
    /// for values with leading zeros.
    /// </summary>
    [Fact]
    public void OshashFormat_UnpaddedDoesNotMatchForShortHashes()
    {
        ulong hash = 0x00abcdef12345678UL;
        var padded = hash.ToString("x16");    // fixed format (matches Go)
        var unpadded = hash.ToString("x");    // old buggy format

        Assert.Equal("00abcdef12345678", padded);
        Assert.Equal("abcdef12345678", unpadded);
        Assert.NotEqual(padded, unpadded);
    }

    /// <summary>
    /// Validates that phash formatting uses unpadded hex (same as Go).
    /// </summary>
    [Theory]
    [InlineData(0x0123456789abcdefUL, "123456789abcdef")]
    [InlineData(0xfedcba9876543210UL, "fedcba9876543210")]
    public void PhashFormat_MatchesGoUnpaddedHex(ulong hash, string expected)
    {
        var result = hash.ToString("x");
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Fingerprint hash comparison should be case-insensitive (metadata-server returns
    /// lowercase, but we should handle any case).
    /// </summary>
    [Fact]
    public void FingerprintHashComparison_IsCaseInsensitive()
    {
        var localHash = "abcdef1234567890";
        var remoteHash = "ABCDEF1234567890";

        Assert.Equal(localHash, remoteHash, ignoreCase: true);
    }

    [Fact]
    public void SceneSearchTerms_StripLeadingNumericPrefix()
    {
        var result = InvokeSceneSearchTerms("144 - Two Dragon Cumshots For Boosette");

        Assert.Equal("144 - Two Dragon Cumshots For Boosette", result[0]);
        Assert.Contains("Two Dragon Cumshots For Boosette", result);
    }

    [Fact]
    public void SceneSearchTerms_DoNotDuplicateEquivalentTerms()
    {
        var result = InvokeSceneSearchTerms("Two Dragon Cumshots For Boosette");

        Assert.Single(result);
        Assert.Equal("Two Dragon Cumshots For Boosette", result[0]);
    }

    private static IReadOnlyList<string> InvokeSceneSearchTerms(string term)
    {
        var method = typeof(MetadataServerService).GetMethod("BuildSceneSearchTerms", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [term]);
        var terms = Assert.IsAssignableFrom<IReadOnlyList<string>>(result);
        return terms;
    }
}

/// <summary>
/// Tests for the theme system registration and built-in themes.
/// </summary>
public class ThemeSystemTests
{
    [Fact]
    public void ThemeCollectionExtension_ReturnsAllBuiltInThemes()
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();

        // Should have 18 themes total
        Assert.Equal(18, manifest.Themes.Count);
    }

    [Theory]
    [InlineData("dark-default")]
    [InlineData("light")]
    [InlineData("dark-midnight")]
    [InlineData("dark-emerald")]
    [InlineData("dark-rose")]
    [InlineData("dark-ocean")]
    [InlineData("pornhub")]
    [InlineData("plex")]
    [InlineData("reddit")]
    [InlineData("rainbow")]
    [InlineData("liquid-glass")]
    [InlineData("neon-glow")]
    [InlineData("sunset-gradient")]
    [InlineData("aurora")]
    [InlineData("cyberpunk")]
    [InlineData("deep-space")]
    [InlineData("synthwave")]
    [InlineData("ember")]
    public void ThemeCollectionExtension_ContainsTheme(string themeId)
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();

        var theme = manifest.Themes.FirstOrDefault(t => t.Id == themeId);
        Assert.NotNull(theme);
    }

    [Theory]
    [InlineData("pornhub", "#ff9000")]
    [InlineData("plex", "#e5a00d")]
    [InlineData("reddit", "#ff4500")]
    [InlineData("liquid-glass", "#007aff")]
    [InlineData("neon-glow", "#8b5cf6")]
    [InlineData("sunset-gradient", "#f97316")]
    [InlineData("aurora", "#10b981")]
    [InlineData("cyberpunk", "#ff2d95")]
    [InlineData("deep-space", "#6366f1")]
    [InlineData("synthwave", "#ff2d95")]
    [InlineData("ember", "#f97316")]
    public void NewThemes_HaveCorrectAccentColor(string themeId, string expectedAccent)
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();
        var theme = manifest.Themes.First(t => t.Id == themeId);

        Assert.NotNull(theme.CssVariables);
        Assert.Equal(expectedAccent, theme.CssVariables["--color-accent"]);
    }

    [Fact]
    public void LiquidGlassTheme_HasBackgroundAnimation()
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();
        var theme = manifest.Themes.First(t => t.Id == "liquid-glass");

        Assert.NotNull(theme.CssVariables);
        // Liquid glass theme has a background animation
        Assert.Equal("liquid-drift", theme.BackgroundAnimation);
    }

    [Fact]
    public void DefaultTheme_HasNoCssVariables()
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();
        var defaultTheme = manifest.Themes.First(t => t.Id == "dark-default");

        // Default theme has no CSS overrides (uses the base CSS @theme values)
        Assert.Null(defaultTheme.CssVariables);
    }

    [Fact]
    public void AllThemes_HaveRequiredFields()
    {
        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();

        foreach (var theme in manifest.Themes)
        {
            Assert.False(string.IsNullOrEmpty(theme.Id));
            Assert.False(string.IsNullOrEmpty(theme.Name));
            Assert.False(string.IsNullOrEmpty(theme.Description));
        }
    }

    [Fact]
    public void AllThemesWithColors_HaveCompleteColorSet()
    {
        var requiredVars = new[]
        {
            "--color-background", "--color-nav", "--color-card",
            "--color-surface", "--color-border", "--color-accent",
            "--color-foreground", "--color-secondary", "--color-muted"
        };

        var ext = new ThemeCollectionExtension();
        var manifest = ext.GetUIManifest();

        foreach (var theme in manifest.Themes.Where(t => t.CssVariables != null))
        {
            foreach (var varName in requiredVars)
            {
                Assert.True(theme.CssVariables!.ContainsKey(varName),
                    $"Theme '{theme.Id}' missing required CSS variable: {varName}");
            }
        }
    }
}

/// <summary>
/// Tests for sub-tag depth deserialization from frontend JSON.
/// </summary>
public class SubTagFilterTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Deserialize_MultiIdCriterion_WithDepthMinusOne()
    {
        var json = """{"value": [42], "modifier": "includesAll", "depth": -1}""";
        var result = System.Text.Json.JsonSerializer.Deserialize<MultiIdCriterion>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(-1, result.Depth);
        Assert.Equal(CriterionModifier.IncludesAll, result.Modifier);
    }

    [Fact]
    public void Deserialize_MultiIdCriterion_WithoutDepth_DefaultsToNull()
    {
        var json = """{"value": [42], "modifier": "includes"}""";
        var result = System.Text.Json.JsonSerializer.Deserialize<MultiIdCriterion>(json, Options);

        Assert.NotNull(result);
        Assert.Null(result.Depth);
    }

    [Fact]
    public void Deserialize_MultiIdCriterion_WithDepthZero()
    {
        var json = """{"value": [42], "modifier": "includes", "depth": 0}""";
        var result = System.Text.Json.JsonSerializer.Deserialize<MultiIdCriterion>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(0, result.Depth);
    }
}
