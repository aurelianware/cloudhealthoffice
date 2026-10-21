using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudHealthOffice.Portal.Theming;

/// <summary>WCAG 2.x contrast math for #RRGGBB colors.</summary>
public static partial class ColorContrast
{
    /// <summary>Minimum ratio for normal-size text (WCAG 2.1 AA).</summary>
    public const double AaText = 4.5;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexPattern();

    public static bool IsHex(string? value) => value is not null && HexPattern().IsMatch(value);

    /// <summary>Contrast ratio between two #RRGGBB colors, from 1 to 21.</summary>
    public static double Ratio(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

        static double Channel(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>Mixes the color toward black by <paramref name="amount"/> (0 to 1).</summary>
    public static string Darken(string hex, double amount)
    {
        var (r, g, b) = Parse(hex);
        var keep = 1 - Math.Clamp(amount, 0, 1);
        return ToHex((int)Math.Round(r * keep), (int)Math.Round(g * keep), (int)Math.Round(b * keep));
    }

    /// <summary>Mixes the color toward white by <paramref name="amount"/> (0 to 1).</summary>
    public static string Lighten(string hex, double amount)
    {
        var (r, g, b) = Parse(hex);
        var t = Math.Clamp(amount, 0, 1);
        return ToHex(
            (int)Math.Round(r + (255 - r) * t),
            (int)Math.Round(g + (255 - g) * t),
            (int)Math.Round(b + (255 - b) * t));
    }

    private static (int R, int G, int B) Parse(string hex)
    {
        if (!IsHex(hex))
            throw new FormatException($"Expected a #RRGGBB color, got '{hex}'.");
        return (
            int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string ToHex(int r, int g, int b)
        => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
}
