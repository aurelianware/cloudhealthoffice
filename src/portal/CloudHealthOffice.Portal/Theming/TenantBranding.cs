namespace CloudHealthOffice.Portal.Theming;

/// <summary>
/// A health plan's own look inside the portal: its name and logo in the app bar and
/// its brand color as the accent. Status colors (paid, pended, denied) are deliberately
/// not brandable, so they mean the same thing for every tenant.
/// </summary>
public sealed record TenantBranding
{
    /// <summary>Name shown in the app bar instead of "Cloud Health Office".</summary>
    public string? DisplayName { get; init; }

    /// <summary>Site-relative path ("/branding/acme.svg") or an https URL.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>#RRGGBB brand color. Ignored unless it meets WCAG AA on the light surfaces.</summary>
    public string? AccentColor { get; init; }
}

public interface ITenantBrandingProvider
{
    TenantBranding? GetBranding(string? tenantId);
}

/// <summary>
/// Reads branding from configuration:
/// <code>
/// "TenantBranding": {
///   "parkland-chp": { "DisplayName": "Parkland Community Health Plan",
///                     "LogoUrl": "/branding/parkland.svg", "AccentColor": "#00539B" }
/// }
/// </code>
/// A stand-in until branding is stored with the tenant record in the tenant service.
/// </summary>
public sealed class ConfigurationTenantBrandingProvider : ITenantBrandingProvider
{
    private readonly IConfiguration _configuration;

    public ConfigurationTenantBrandingProvider(IConfiguration configuration) => _configuration = configuration;

    public TenantBranding? GetBranding(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return null;

        var section = _configuration.GetSection("TenantBranding").GetSection(tenantId);
        if (!section.Exists())
            return null;

        return new TenantBranding
        {
            DisplayName = section["DisplayName"],
            LogoUrl = section["LogoUrl"],
            AccentColor = section["AccentColor"],
        };
    }
}

/// <summary>Validates tenant branding and turns it into CSS token overrides.</summary>
public static class TenantBrandingStyle
{
    /// <summary>
    /// Returns the accent if it is a #RRGGBB color with at least AA text contrast against
    /// both the card surface and the page canvas of the Enterprise theme; otherwise null.
    /// </summary>
    public static string? ValidAccent(string? accent)
    {
        if (!ColorContrast.IsHex(accent))
            return null;

        var worst = Math.Min(
            ColorContrast.Ratio(accent!, ChoThemes.EnterpriseSurface),
            ColorContrast.Ratio(accent!, ChoThemes.EnterpriseCanvas));
        return worst >= ColorContrast.AaText ? accent!.ToUpperInvariant() : null;
    }

    /// <summary>A logo URL is accepted only if it is site-relative or https.</summary>
    public static string? ValidLogoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            return url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;
    }

    /// <summary>
    /// CSS that re-points the accent tokens at the tenant color for the Enterprise theme.
    /// The selector outranks the theme blocks in theme-tokens.css. Sentinel keeps its own
    /// accent because a brand color picked for white backgrounds rarely works on black.
    /// </summary>
    public static string? AccentCss(string? accent)
    {
        var valid = ValidAccent(accent);
        if (valid is null)
            return null;

        var hover = ColorContrast.Darken(valid, 0.2);
        var soft = ColorContrast.Lighten(valid, 0.9);
        return "html:root[data-cho-theme=\"enterprise\"]{"
            + $"--cho-accent:{valid};--cho-accent-hover:{hover};--cho-accent-soft:{soft};"
            + "}";
    }
}
