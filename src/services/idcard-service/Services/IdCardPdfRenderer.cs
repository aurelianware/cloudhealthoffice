using System.Globalization;
using System.Text;
using IdCardService.Models;
using SkiaSharp;

namespace IdCardService.Services;

/// <summary>
/// Lays out a member ID card as a one-page PDF (more pages only if the content
/// does not fit) with SkiaSharp's built-in PDF backend. SkiaSharp is MIT
/// licensed, so the PDF path carries no commercial licence terms.
/// </summary>
/// <remarks>
/// Text uses Noto Sans (SIL OFL 1.1), embedded in this assembly so cards render
/// the same in a container with no system fonts. SkiaSharp 2.88 embeds whole
/// font files rather than subsetting them, so the bundled fonts are a Latin
/// subset (scripts/fonts/subset-idcard-fonts.sh), which keeps a card near
/// 100 KB. Characters outside it fall back to a system font when one has them.
/// Text is drawn without complex-script shaping, which is right for Latin.
/// </remarks>
internal static class IdCardPdfRenderer
{
    // A6 landscape (148 x 105 mm) in PDF points.
    internal const float PageWidth = 148f / 25.4f * 72f;
    internal const float PageHeight = 105f / 25.4f * 72f;
    private const float Margin = 16f;
    private const float QrSize = 80f;
    private const float ContentPadding = 8f;
    private const float FieldSpacing = 4f;
    private const float FooterSpacing = 2f;

    private static readonly SKColor Ink = SKColors.Black;
    private static readonly SKColor Muted = new(0x61, 0x61, 0x61);

    public static byte[] Render(IdCardTemplate template, CardBindings b, byte[] qrPng)
    {
        using var qr = DecodeQr(qrPng);

        var contentWidth = PageWidth - 2 * Margin;
        var header = new List<Block>
        {
            // The issuing entity heads the card where the carrier states one,
            // and the sponsor where it does not, which is what every card was
            // headed by before carriers existed (plan-configuration.md §12.4).
            Block.Of(contentWidth - QrSize, new Run(b.IssuerName ?? b.SponsorName ?? "Member ID Card", Style.SemiBold(14))),
            Block.Of(contentWidth - QrSize, new Run(b.PlanName ?? string.Empty, Style.Regular(10))),
        };

        var fields = new List<Block>();
        void Field(string label, string? value, bool always = false)
        {
            if (always || !string.IsNullOrEmpty(value))
                fields.Add(Block.Of(contentWidth, new Run(label + ": ", Style.SemiBold(10)), new Run(value ?? string.Empty, Style.Regular(10))));
        }
        Field("Member", b.MemberName, always: true);
        Field("Member ID", b.MemberNumber, always: true);
        Field("Group", b.GroupNumber);
        // Conditional like the rest, so a card for a member enrolled by group
        // number -- every card until a tenant enters policies -- is laid out
        // exactly as it is today.
        Field("Policy", b.SponsorPolicyNumber);
        Field("Plan", b.PlanId);
        Field("Network", b.NetworkName);
        Field("Copays", b.CopaySummary);
        if (b.EffectiveDate.HasValue)
            Field("Effective", b.EffectiveDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var footer = new List<Block>();
        if (!string.IsNullOrEmpty(template.BackText))
            footer.Add(Block.Of(contentWidth, new Run(template.BackText, Style.Regular(7))));
        foreach (var disclaimer in template.Disclaimers)
            footer.Add(Block.Of(contentWidth, new Run(disclaimer, Style.Regular(7))));
        if (!string.IsNullOrEmpty(b.IssuerAddress))
            footer.Add(Block.Of(contentWidth, new Run(b.IssuerAddress, Style.Regular(7, Muted))));
        footer.Add(Block.Of(contentWidth, new Run(
            $"Card {b.CardId} • Issued {b.IssuedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", Style.Regular(7, Muted))));

        var headerHeight = Math.Max(header.Sum(x => x.Height), qr == null ? 0 : QrSize);
        var footerHeight = Stack(footer, FooterSpacing);
        var bodyTop = Margin + headerHeight + ContentPadding;
        // A footer too tall to pin under the header (many long disclaimers) flows
        // after the fields instead, so nothing ever overlaps.
        var smallestField = fields.Min(f => f.Height);
        var pinFooter = PageHeight - Margin - footerHeight - ContentPadding - bodyTop >= smallestField;
        var bodyBottom = pinFooter ? PageHeight - Margin - footerHeight - ContentPadding : PageHeight - Margin;
        var flow = pinFooter ? fields : fields.Concat(footer).ToList();

        using var stream = new MemoryStream();
        // Start from Default: SKDocumentPdfMetadata is a struct, and a bare
        // initializer leaves EncodingQuality at 0, which JPEG-compresses images
        // at the lowest quality and degrades the QR code. 101 is lossless.
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = "Member ID Card";
        metadata.Creator = "Cloud Health Office idcard-service";
        metadata.Producer = "Cloud Health Office";
        metadata.Creation = b.IssuedAt;
        metadata.Modified = b.IssuedAt;
        metadata.RasterDpi = 300;
        metadata.EncodingQuality = 101;
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            SKCanvas BeginPage()
            {
                var canvas = document.BeginPage(PageWidth, PageHeight);
                canvas.Clear(SKColors.White);
                var y = Margin;
                foreach (var block in header)
                {
                    block.Draw(canvas, Margin, y);
                    y += block.Height;
                }
                if (qr != null)
                    DrawFitted(canvas, qr, SKRect.Create(PageWidth - Margin - QrSize, Margin, QrSize, QrSize));
                if (pinFooter)
                {
                    var fy = PageHeight - Margin - footerHeight;
                    foreach (var block in footer)
                    {
                        block.Draw(canvas, Margin, fy);
                        fy += block.Height + FooterSpacing;
                    }
                }
                return canvas;
            }

            var page = BeginPage();
            var cursor = bodyTop;
            var first = true;
            foreach (var block in flow)
            {
                // A block that runs past the bottom continues, line by line,
                // on the next page under the repeated header.
                foreach (var line in block.Lines)
                {
                    var gap = first ? 0 : (line == block.Lines[0] ? FieldSpacing : 0);
                    if (cursor + gap + line.Height > bodyBottom && cursor > bodyTop)
                    {
                        document.EndPage();
                        page = BeginPage();
                        cursor = bodyTop;
                        gap = 0;
                    }
                    cursor += gap;
                    line.Draw(page, Margin, cursor);
                    cursor += line.Height;
                    first = false;
                }
            }
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    private static float Stack(IReadOnlyList<Block> blocks, float spacing) =>
        blocks.Sum(x => x.Height) + Math.Max(0, blocks.Count - 1) * spacing;

    private static SKImage? DecodeQr(byte[] qrPng)
    {
        if (qrPng is not { Length: > 0 }) return null;
        return SKImage.FromEncodedData(qrPng)
            ?? throw new InvalidOperationException("QR code image could not be decoded");
    }

    private static void DrawFitted(SKCanvas canvas, SKImage image, SKRect box)
    {
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        var dest = SKRect.Create(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);
        // Nearest-neighbour keeps the QR modules sharp when scaled.
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
        canvas.DrawImage(image, dest, paint);
    }

    internal sealed record Style(SKTypeface Typeface, float Size, SKColor Color)
    {
        public static Style Regular(float size, SKColor? color = null) => new(IdCardFonts.Regular, size, color ?? Ink);
        public static Style SemiBold(float size, SKColor? color = null) => new(IdCardFonts.SemiBold, size, color ?? Ink);
    }

    internal sealed record Run(string Text, Style Style);

    /// <summary>A paragraph of styled runs, word-wrapped to a width.</summary>
    internal sealed class Block
    {
        public required IReadOnlyList<Line> Lines { get; init; }
        public float Height => Lines.Sum(l => l.Height);

        public static Block Of(float width, params Run[] runs) => new() { Lines = Wrap(runs, width) };

        public void Draw(SKCanvas canvas, float x, float y)
        {
            foreach (var line in Lines)
            {
                line.Draw(canvas, x, y);
                y += line.Height;
            }
        }
    }

    internal sealed record Piece(string Text, SKTypeface Typeface, Style Style, float Width);

    internal sealed class Line
    {
        public List<Piece> Pieces { get; } = new();
        public float Height { get; set; }
        public float Ascent { get; set; }

        public void Draw(SKCanvas canvas, float x, float top)
        {
            var baseline = top + Ascent;
            foreach (var piece in Pieces)
            {
                using var paint = TextPaint(piece.Typeface, piece.Style.Size);
                paint.Color = piece.Style.Color;
                canvas.DrawText(piece.Text, x, baseline, paint);
                x += piece.Width;
            }
        }
    }

    /// <summary>Greedy word wrap across runs; a word wider than the line is broken by character.</summary>
    internal static List<Line> Wrap(IEnumerable<Run> runs, float width)
    {
        var lines = new List<Line>();
        var line = new Line();
        var x = 0f;

        void Measure(Line l, Style style)
        {
            using var paint = TextPaint(style.Typeface, style.Size);
            l.Height = Math.Max(l.Height, paint.FontSpacing);
            l.Ascent = Math.Max(l.Ascent, -paint.FontMetrics.Ascent);
        }
        void Break(Style style)
        {
            if (line.Height == 0) Measure(line, style);
            lines.Add(line);
            line = new Line();
            x = 0;
        }
        void Add(string text, Style style)
        {
            foreach (var (segment, face) in Segment(text, style.Typeface))
            {
                using var paint = TextPaint(face, style.Size);
                var w = paint.MeasureText(segment);
                line.Pieces.Add(new Piece(segment, face, style, w));
                x += w;
            }
            Measure(line, style);
        }

        Style? last = null;
        foreach (var run in runs)
        {
            last = run.Style;
            foreach (var token in Tokens(run.Text))
            {
                var isSpace = token == " ";
                if (isSpace && x == 0) continue; // no leading space on a wrapped line
                var w = Width(token, run.Style);
                if (!isSpace && x > 0 && x + w > width)
                {
                    TrimTrailingSpace(line, ref x);
                    Break(run.Style);
                }
                if (!isSpace && w > width)
                {
                    foreach (var chunk in SplitToWidth(token, run.Style, width))
                    {
                        if (x > 0) Break(run.Style);
                        Add(chunk, run.Style);
                    }
                    continue;
                }
                Add(token, run.Style);
            }
        }
        TrimTrailingSpace(line, ref x);
        if (line.Pieces.Count > 0 || lines.Count == 0) Break(last ?? Style.Regular(10));
        return lines;
    }

    private static SKPaint TextPaint(SKTypeface typeface, float size) => new()
    {
        Typeface = typeface, TextSize = size, IsAntialias = true, SubpixelText = true, TextEncoding = SKTextEncoding.Utf16,
    };

    private static void TrimTrailingSpace(Line line, ref float x)
    {
        while (line.Pieces.Count > 0 && line.Pieces[^1].Text == " ")
        {
            x -= line.Pieces[^1].Width;
            line.Pieces.RemoveAt(line.Pieces.Count - 1);
        }
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\t', ' '))
        {
            if (c == ' ')
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return " ";
            }
            else word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static float Width(string text, Style style)
    {
        var total = 0f;
        foreach (var (segment, face) in Segment(text, style.Typeface))
        {
            using var paint = TextPaint(face, style.Size);
            total += paint.MeasureText(segment);
        }
        return total;
    }

    private static IEnumerable<string> SplitToWidth(string word, Style style, float width)
    {
        var chunk = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            if (chunk.Length > 0 && Width(chunk + element, style) > width)
            {
                yield return chunk.ToString();
                chunk.Clear();
            }
            chunk.Append(element);
        }
        if (chunk.Length > 0) yield return chunk.ToString();
    }

    /// <summary>Splits text into runs drawable by the primary typeface or a system fallback.</summary>
    internal static IEnumerable<(string Text, SKTypeface Typeface)> Segment(string text, SKTypeface primary)
    {
        var current = new StringBuilder();
        SKTypeface? face = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var chosen = primary.ContainsGlyph(rune.Value) ? primary : IdCardFonts.Fallback(rune.Value, primary);
            if (face != null && chosen != face)
            {
                yield return (current.ToString(), face);
                current.Clear();
            }
            face = chosen;
            current.Append(rune.ToString());
        }
        if (face != null) yield return (current.ToString(), face);
    }
}

/// <summary>Noto Sans (SIL OFL 1.1, see Fonts/OFL.txt), loaded once from embedded resources.</summary>
internal static class IdCardFonts
{
    private static readonly Lazy<SKTypeface> _regular = new(() => Load("IdCardService.Fonts.NotoSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> _semiBold = new(() => Load("IdCardService.Fonts.NotoSans-SemiBold.ttf"));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, string), SKTypeface> _fallbacks = new();

    public static SKTypeface Regular => _regular.Value;
    public static SKTypeface SemiBold => _semiBold.Value;

    /// <summary>A system typeface with the character, or the primary (which draws a missing-glyph box).</summary>
    public static SKTypeface Fallback(int codepoint, SKTypeface primary) =>
        _fallbacks.GetOrAdd((codepoint, primary.FamilyName + primary.FontWeight), _ =>
            SKFontManager.Default.MatchCharacter(null, primary.FontStyle, null, codepoint) ?? primary);

    private static SKTypeface Load(string resource)
    {
        using var stream = typeof(IdCardFonts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded font missing: " + resource);
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException("Embedded font could not be loaded: " + resource);
    }
}
