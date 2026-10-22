using System.Collections.Concurrent;
using SkiaSharp;

namespace CloudHealthOffice.PdfLayout;

/// <summary>
/// Noto Sans (SIL OFL 1.1, see Fonts/OFL.txt), loaded once from embedded
/// resources so PDFs render the same in a container with no system fonts.
/// The files are a Latin subset (Basic through Extended Additional, which
/// covers English, Spanish, Vietnamese, Tagalog, French and Haitian Creole):
/// SkiaSharp 2.88 embeds whole font files rather than subsetting them.
/// Characters outside the subset fall back to a system font that has them.
/// </summary>
public static class PdfFonts
{
    private static readonly Lazy<SKTypeface> RegularFace = new(() => Load("CloudHealthOffice.PdfLayout.Fonts.NotoSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> SemiBoldFace = new(() => Load("CloudHealthOffice.PdfLayout.Fonts.NotoSans-SemiBold.ttf"));
    private static readonly ConcurrentDictionary<(int, string), SKTypeface> Fallbacks = new();

    public static SKTypeface Regular => RegularFace.Value;
    public static SKTypeface SemiBold => SemiBoldFace.Value;

    /// <summary>A system typeface with the character, or the primary (which draws a missing-glyph box).</summary>
    public static SKTypeface Fallback(int codepoint, SKTypeface primary) =>
        Fallbacks.GetOrAdd((codepoint, primary.FamilyName + primary.FontWeight), _ =>
            SKFontManager.Default.MatchCharacter(null, primary.FontStyle, null, codepoint) ?? primary);

    private static SKTypeface Load(string resource)
    {
        using var stream = typeof(PdfFonts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded font missing: " + resource);
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException("Embedded font could not be loaded: " + resource);
    }
}
