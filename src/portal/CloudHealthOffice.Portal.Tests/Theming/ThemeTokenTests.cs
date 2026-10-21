using System.Text.RegularExpressions;
using CloudHealthOffice.Portal.Theming;

namespace CloudHealthOffice.Portal.Tests.Theming;

/// <summary>
/// The portal paints from CSS tokens (theme-tokens.css, legacy-color-bridge.css) and
/// MudBlazor paints from ChoThemes. These tests keep the two in step, keep the
/// Enterprise palette readable, and stop hardcoded Sentinel colors coming back.
/// </summary>
public class ThemeTokenTests
{
    private static readonly string PortalDir = FindPortalDir();

    private static string FindPortalDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "portal", "CloudHealthOffice.Portal");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("src/portal/CloudHealthOffice.Portal");
    }

    private static string Css(string name) => File.ReadAllText(Path.Combine(PortalDir, "wwwroot", "css", name));

    /// <summary>Custom properties declared in the first rule whose selector contains <paramref name="selectorFragment"/>.</summary>
    private static Dictionary<string, string> Block(string css, string selectorFragment)
    {
        var start = css.IndexOf(selectorFragment, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"selector {selectorFragment} should exist");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return Regex.Matches(css[(open + 1)..close], @"(--[a-z0-9-_]+)\s*:\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    }

    private static IEnumerable<string> PortalSources()
        => Directory.EnumerateFiles(PortalDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".razor.css") || f.EndsWith(".cs"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Both_themes_define_the_same_semantic_tokens()
    {
        var css = Css("theme-tokens.css");
        var enterprise = Block(css, ":root[data-cho-theme=\"enterprise\"]");
        var sentinel = Block(css, ":root[data-cho-theme=\"sentinel\"]");

        sentinel.Keys.Should().BeEquivalentTo(enterprise.Keys);
    }

    [Theory]
    [InlineData("--cho-ink")]
    [InlineData("--cho-text")]
    [InlineData("--cho-text-muted")]
    [InlineData("--cho-text-subtle")]
    [InlineData("--cho-accent")]
    [InlineData("--cho-accent-hover")]
    [InlineData("--cho-accent-2")]
    [InlineData("--cho-success")]
    [InlineData("--cho-warning")]
    [InlineData("--cho-error")]
    [InlineData("--cho-info")]
    public void Enterprise_text_colors_meet_WCAG_AA_on_cards_and_canvas(string token)
    {
        var enterprise = Block(Css("theme-tokens.css"), ":root[data-cho-theme=\"enterprise\"]");
        var color = enterprise[token];

        ColorContrast.Ratio(color, enterprise["--cho-surface"]).Should().BeGreaterThanOrEqualTo(ColorContrast.AaText);
        ColorContrast.Ratio(color, enterprise["--cho-bg"]).Should().BeGreaterThanOrEqualTo(ColorContrast.AaText);
    }

    [Fact]
    public void App_bar_text_is_readable_on_the_navy_bar()
    {
        var enterprise = Block(Css("theme-tokens.css"), ":root[data-cho-theme=\"enterprise\"]");

        ColorContrast.Ratio(enterprise["--cho-on-appbar"], enterprise["--cho-appbar"]).Should().BeGreaterThanOrEqualTo(ColorContrast.AaText);
        ColorContrast.Ratio(enterprise["--cho-on-appbar-muted"], enterprise["--cho-appbar"]).Should().BeGreaterThanOrEqualTo(ColorContrast.AaText);
    }

    [Fact]
    public void MudBlazor_enterprise_palette_matches_the_css_tokens()
    {
        var enterprise = Block(Css("theme-tokens.css"), ":root[data-cho-theme=\"enterprise\"]");
        var palette = ChoThemes.Create(ChoThemeNames.Enterprise).Palette;

        palette.Primary.Value.Should().StartWithEquivalentOf(enterprise["--cho-accent"]);
        palette.Surface.Value.Should().StartWithEquivalentOf(enterprise["--cho-surface"]);
        palette.Background.Value.Should().StartWithEquivalentOf(enterprise["--cho-bg"]);
        palette.AppbarBackground.Value.Should().StartWithEquivalentOf(enterprise["--cho-appbar"]);
        palette.TextPrimary.Value.Should().StartWithEquivalentOf(enterprise["--cho-ink"]);
        palette.Success.Value.Should().StartWithEquivalentOf(enterprise["--cho-success"]);
        palette.Error.Value.Should().StartWithEquivalentOf(enterprise["--cho-error"]);
        palette.Warning.Value.Should().StartWithEquivalentOf(enterprise["--cho-warning"]);
    }

    [Fact]
    public void Every_bridge_token_a_page_uses_is_defined_for_both_themes()
    {
        var bridge = Css("legacy-color-bridge.css");
        var enterprise = Block(bridge, ":root[data-cho-theme=\"enterprise\"]");
        var sentinel = Block(bridge, ":root[data-cho-theme=\"sentinel\"]");

        var used = PortalSources()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"var\((--cb-[a-z0-9_-]+)\)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        used.Should().NotBeEmpty();
        enterprise.Keys.Should().Contain(used);
        sentinel.Keys.Should().Contain(used);
    }

    [Fact]
    public void Every_semantic_token_a_page_uses_is_defined()
    {
        var tokens = Css("theme-tokens.css");
        var defined = Regex.Matches(tokens, @"(--cho-[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();

        var used = PortalSources()
            .Concat(new[] { Path.Combine(PortalDir, "wwwroot", "css", "site.css"), Path.Combine(PortalDir, "wwwroot", "css", "legacy-color-bridge.css") })
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"var\((--cho-[a-z0-9-]+)").Select(m => m.Groups[1].Value))
            .Distinct();

        defined.Should().Contain(used);
    }

    /// <summary>
    /// Sentinel neon literals belong only in the theme files. A page that hardcodes one
    /// renders cyan-on-white in the Enterprise theme. Use var(--cho-accent) and friends.
    /// </summary>
    [Fact]
    public void Pages_do_not_hardcode_Sentinel_colors()
    {
        var neon = new Regex(
            @"#(00ffff|00ff88|ff0055|ffaa00)\b|rgba?\(\s*(0\s*,\s*255\s*,\s*255|0\s*,\s*255\s*,\s*136|255\s*,\s*0\s*,\s*85|255\s*,\s*170\s*,\s*0|255\s*,\s*255\s*,\s*255)\b",
            RegexOptions.IgnoreCase);
        var allowed = new[]
        {
            Path.Combine("Theming", "ChoThemes.cs"),
            // Public marketing pages with their own fixed look; not part of the app theme.
            Path.Combine("Pages", "Welcome.razor.css"),
            Path.Combine("Pages", "Signup.razor.css"),
        };

        var offenders = PortalSources()
            .Where(f => !allowed.Any(a => f.EndsWith(a, StringComparison.Ordinal)))
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (line, i))
                .Where(x => neon.IsMatch(x.line))
                .Select(x => $"{Path.GetRelativePath(PortalDir, f)}:{x.i + 1}"))
            .ToList();

        offenders.Should().BeEmpty("pages should use theme tokens, not Sentinel literals");
    }
}
