using System.Globalization;
using System.Text;
using SkiaSharp;

namespace CloudHealthOffice.PdfLayout;

/// <summary>A typeface, size (points) and colour.</summary>
public sealed record TextStyle(SKTypeface Typeface, float Size, SKColor Color)
{
    public static TextStyle Regular(float size, SKColor? color = null) => new(PdfFonts.Regular, size, color ?? SKColors.Black);
    public static TextStyle SemiBold(float size, SKColor? color = null) => new(PdfFonts.SemiBold, size, color ?? SKColors.Black);
}

/// <summary>A piece of text in one style.</summary>
public sealed record TextRun(string Text, TextStyle Style);

/// <summary>A drawable fragment of a line in one typeface.</summary>
public sealed record TextPiece(string Text, SKTypeface Typeface, TextStyle Style, float Width);

/// <summary>One wrapped line.</summary>
public sealed class TextLine
{
    public List<TextPiece> Pieces { get; } = new();
    public float Height { get; internal set; }
    public float Ascent { get; internal set; }
    public float Width => Pieces.Sum(p => p.Width);

    /// <summary>The line's text, as drawn.</summary>
    public string Text => string.Concat(Pieces.Select(p => p.Text));

    /// <summary>The smallest font size on the line.</summary>
    public float MinSize => Pieces.Count == 0 ? float.MaxValue : Pieces.Min(p => p.Style.Size);

    public void Draw(SKCanvas canvas, float x, float top)
    {
        var baseline = top + Ascent;
        foreach (var piece in Pieces)
        {
            using var paint = TextLayout.Paint(piece.Typeface, piece.Style.Size);
            paint.Color = piece.Style.Color;
            canvas.DrawText(piece.Text, x, baseline, paint);
            x += piece.Width;
        }
    }
}

/// <summary>
/// Greedy word wrap across styled runs. Text is drawn without complex-script
/// shaping, which is right for the Latin scripts the embedded fonts carry.
/// </summary>
public static class TextLayout
{
    /// <summary>Wraps <paramref name="runs"/> to <paramref name="width"/>; a word wider than the line is broken by character. "\n" starts a new line.</summary>
    public static List<TextLine> Wrap(IEnumerable<TextRun> runs, float width)
    {
        var lines = new List<TextLine>();
        var line = new TextLine();
        var x = 0f;

        void Measure(TextLine l, TextStyle style)
        {
            using var paint = Paint(style.Typeface, style.Size);
            l.Height = Math.Max(l.Height, paint.FontSpacing);
            l.Ascent = Math.Max(l.Ascent, -paint.FontMetrics.Ascent);
        }
        void Break(TextStyle style)
        {
            if (line.Height == 0) Measure(line, style);
            lines.Add(line);
            line = new TextLine();
            x = 0;
        }
        void Add(string text, TextStyle style)
        {
            foreach (var (segment, face) in Segment(text, style.Typeface))
            {
                using var paint = Paint(face, style.Size);
                var w = paint.MeasureText(segment);
                line.Pieces.Add(new TextPiece(segment, face, style, w));
                x += w;
            }
            Measure(line, style);
        }

        TextStyle? last = null;
        foreach (var run in runs)
        {
            last = run.Style;
            foreach (var token in Tokens(run.Text))
            {
                if (token == "\n")
                {
                    TrimTrailingSpace(line, ref x);
                    Break(run.Style);
                    continue;
                }
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
        if (line.Pieces.Count > 0 || lines.Count == 0) Break(last ?? TextStyle.Regular(10));
        return lines;
    }

    public static float Width(string text, TextStyle style)
    {
        var total = 0f;
        foreach (var (segment, face) in Segment(text, style.Typeface))
        {
            using var paint = Paint(face, style.Size);
            total += paint.MeasureText(segment);
        }
        return total;
    }

    internal static SKPaint Paint(SKTypeface typeface, float size) => new()
    {
        Typeface = typeface, TextSize = size, IsAntialias = true, SubpixelText = true, TextEncoding = SKTextEncoding.Utf16,
    };

    private static void TrimTrailingSpace(TextLine line, ref float x)
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
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' '))
        {
            if (c is ' ' or '\n')
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return c.ToString();
            }
            else word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static IEnumerable<string> SplitToWidth(string word, TextStyle style, float width)
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
    public static IEnumerable<(string Text, SKTypeface Typeface)> Segment(string text, SKTypeface primary)
    {
        var current = new StringBuilder();
        SKTypeface? face = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var chosen = primary.ContainsGlyph(rune.Value) ? primary : PdfFonts.Fallback(rune.Value, primary);
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
