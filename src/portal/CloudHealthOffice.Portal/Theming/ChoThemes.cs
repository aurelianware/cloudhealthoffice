using MudBlazor;

namespace CloudHealthOffice.Portal.Theming;

/// <summary>
/// Theme names. The value is written to &lt;html data-cho-theme="..."&gt; and to the
/// <c>cho-theme</c> cookie, and selects a token set in wwwroot/css/theme-tokens.css.
/// </summary>
public static class ChoThemeNames
{
    public const string Enterprise = "enterprise";
    public const string Sentinel = "sentinel";
    public const string CookieName = "cho-theme";

    public static IReadOnlyList<string> All { get; } = new[] { Enterprise, Sentinel };

    /// <summary>Returns a known theme name, falling back to <paramref name="fallback"/> and then Enterprise.</summary>
    public static string Normalize(string? value, string? fallback = null)
    {
        foreach (var candidate in new[] { value, fallback })
        {
            var trimmed = candidate?.Trim().ToLowerInvariant();
            if (trimmed is Enterprise or Sentinel)
                return trimmed;
        }
        return Enterprise;
    }

    public static string DisplayName(string name) => name switch
    {
        Sentinel => "Sentinel (dark)",
        _ => "Enterprise (light)",
    };
}

/// <summary>
/// MudBlazor themes that mirror the CSS token sets. Keep the hex values here in step
/// with theme-tokens.css: MudBlazor paints its own components from this palette, and
/// page styles paint from the CSS variables.
/// </summary>
public static class ChoThemes
{
    // Enterprise palette (matches src/site/css/enterprise.css)
    public const string EnterpriseAccent = "#0E7490";
    public const string EnterpriseSurface = "#FFFFFF";
    public const string EnterpriseCanvas = "#F5F7FA";

    private static readonly string[] EnterpriseFonts =
        { "IBM Plex Sans", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif" };

    private static readonly string[] SentinelFonts =
        { "Segoe UI", "-apple-system", "BlinkMacSystemFont", "Roboto", "Helvetica Neue", "Arial", "sans-serif" };

    /// <summary>
    /// Builds the MudTheme for <paramref name="themeName"/>. A tenant accent (already
    /// validated by <see cref="TenantBrandingStyle"/>) replaces Primary in Enterprise only.
    /// </summary>
    public static MudTheme Create(string themeName, string? tenantAccent = null)
        => ChoThemeNames.Normalize(themeName) == ChoThemeNames.Sentinel
            ? Sentinel()
            : Enterprise(tenantAccent);

    /// <summary>Series colors for MudChart, which takes literal colors rather than CSS variables.</summary>
    public static string[] OutcomeChartPalette(string themeName)
        => ChoThemeNames.Normalize(themeName) == ChoThemeNames.Sentinel
            ? new[] { "#00ff88", "#ff0055", "#ffaa00" }
            : new[] { "#15803D", "#B91C1C", "#B45309" };

    private static MudTheme Enterprise(string? tenantAccent) => new()
    {
        Palette = new PaletteLight
        {
            Primary = tenantAccent ?? EnterpriseAccent,
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#0B2545",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#0F766E",
            Info = "#0369A1",
            Success = "#15803D",
            Warning = "#B45309",
            Error = "#B91C1C",
            Dark = "#0B1F33",

            Background = EnterpriseCanvas,
            BackgroundGrey = "#EEF3F8",
            Surface = EnterpriseSurface,
            DrawerBackground = "#FFFFFF",
            DrawerText = "#334155",
            DrawerIcon = "#475569",
            AppbarBackground = "#0B2545",
            AppbarText = "#FFFFFF",

            TextPrimary = "#0B1F33",
            TextSecondary = "#475569",
            TextDisabled = "#94A3B8",

            ActionDefault = "#475569",
            ActionDisabled = "rgba(11, 31, 51, 0.26)",
            ActionDisabledBackground = "rgba(11, 31, 51, 0.08)",
            HoverOpacity = 0.06,

            LinesDefault = "#D9E1EA",
            LinesInputs = "#B8C4D2",
            TableLines = "#D9E1EA",
            TableStriped = "#F5F7FA",
            TableHover = "#E6F2F5",
            Divider = "#D9E1EA",
            DividerLight = "#EEF3F8",
        },
        Typography = new Typography
        {
            Default = new Default { FontFamily = EnterpriseFonts },
            H1 = new H1 { FontFamily = EnterpriseFonts, FontWeight = 700 },
            H2 = new H2 { FontFamily = EnterpriseFonts, FontWeight = 700 },
            H3 = new H3 { FontFamily = EnterpriseFonts, FontWeight = 700 },
            H4 = new H4 { FontFamily = EnterpriseFonts, FontWeight = 700 },
            H5 = new H5 { FontFamily = EnterpriseFonts, FontWeight = 600 },
            H6 = new H6 { FontFamily = EnterpriseFonts, FontWeight = 600 },
            Button = new Button { FontFamily = EnterpriseFonts, FontWeight = 600, TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
    };

    // The original Sentinel palette, unchanged.
    private static MudTheme Sentinel() => new()
    {
        Palette = new PaletteLight
        {
            Primary = "#00ffff",
            Secondary = "#00ff88",
            Tertiary = "#00ddbb",

            Background = "#000000",
            BackgroundGrey = "#0a0a0a",
            Surface = "#0f0f0f",
            DrawerBackground = "#000000",
            AppbarBackground = "#000000",

            TextPrimary = "#ffffff",
            TextSecondary = "rgba(255, 255, 255, 0.7)",
            TextDisabled = "rgba(255, 255, 255, 0.38)",

            Success = "#00ff88",
            Error = "#ff0055",
            Warning = "#ffaa00",
            Info = "#00ffff",

            Divider = "rgba(0, 255, 255, 0.2)",
            DividerLight = "rgba(0, 255, 255, 0.1)",

            ActionDefault = "rgba(0, 255, 255, 0.54)",
            ActionDisabled = "rgba(255, 255, 255, 0.26)",
            ActionDisabledBackground = "rgba(255, 255, 255, 0.12)",

            HoverOpacity = 0.15,

            LinesDefault = "rgba(0, 255, 255, 0.2)",
            LinesInputs = "rgba(0, 255, 255, 0.3)",
        },
        Typography = new Typography
        {
            Default = new Default { FontFamily = SentinelFonts },
        },
    };
}
