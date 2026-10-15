using System.Globalization;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Resolves the portal's display culture. Containers typically run without LANG,
/// which leaves .NET on the invariant culture (currency shown as "¤").
/// </summary>
public static class PortalCulture
{
    public const string Default = "en-US";

    public static CultureInfo Resolve(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? Default : configured.Trim();
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            // Invariant, and the synthetic culture ICU builds for an unknown name,
            // have no real currency: their symbol is the generic "¤".
            return culture.NumberFormat.CurrencySymbol == "\u00A4" ? CultureInfo.GetCultureInfo(Default) : culture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(Default);
        }
    }
}
