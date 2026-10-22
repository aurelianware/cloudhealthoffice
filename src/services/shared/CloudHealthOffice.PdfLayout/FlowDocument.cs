using SkiaSharp;

namespace CloudHealthOffice.PdfLayout;

/// <summary>Page geometry in PDF points (1/72 inch).</summary>
public sealed record PageSpec(float Width, float Height, float MarginLeft, float MarginTop, float MarginRight, float MarginBottom)
{
    /// <summary>US Letter, 1-inch margins (0.75-inch bottom, leaving room for the footer).</summary>
    public static PageSpec UsLetter { get; } = new(612f, 792f, 72f, 72f, 72f, 54f);

    public float ContentWidth => Width - MarginLeft - MarginRight;
}

/// <summary>A paragraph in the flow: wrapped lines plus spacing and page control.</summary>
public sealed class FlowBlock
{
    public required IReadOnlyList<TextLine> Lines { get; init; }

    /// <summary>Space above the block, in points; dropped at the top of a page.</summary>
    public float SpaceBefore { get; init; }

    /// <summary>Start the block on a new page.</summary>
    public bool PageBreakBefore { get; init; }

    /// <summary>Move the whole block to the next page rather than split it (when it fits on one page).</summary>
    public bool KeepTogether { get; init; }

    /// <summary>Left indent in points.</summary>
    public float Indent { get; init; }

    public float Height => Lines.Sum(l => l.Height);

    public static FlowBlock Of(float width, params TextRun[] runs) => new() { Lines = TextLayout.Wrap(runs, width) };
}

/// <summary>Document-level PDF metadata.</summary>
public sealed record PdfInfo(string Title, string Creator, DateTime CreatedAt, string? Subject = null);

/// <summary>
/// Lays blocks out top to bottom across as many pages as they need, with a
/// footer on every page ("Page n of m" plus optional text), and writes a PDF
/// with SkiaSharp's PDF backend (MIT licensed). Layout is done once, before
/// drawing, so the total page count is known for every footer.
/// </summary>
public static class FlowDocument
{
    public const float FooterSize = 8f;

    /// <summary>Where each line lands: page index and top offset. Exposed for tests and checks.</summary>
    public sealed record Placement(int Page, float Top, float Left, TextLine Line);

    public static IReadOnlyList<Placement> Layout(PageSpec page, IReadOnlyList<FlowBlock> blocks)
    {
        var placements = new List<Placement>();
        var footerReserve = FooterSize * 2f;
        var top = page.MarginTop;
        var bottom = page.Height - page.MarginBottom - footerReserve;
        var pageIndex = 0;
        var y = top;

        foreach (var block in blocks)
        {
            if (block.PageBreakBefore && placements.Count > 0 && placements[^1].Page == pageIndex)
            {
                pageIndex++;
                y = top;
            }

            var gap = y > top ? block.SpaceBefore : 0f;
            if (block.KeepTogether && block.Height <= bottom - top && y + gap + block.Height > bottom && y > top)
            {
                pageIndex++;
                y = top;
                gap = 0;
            }
            y += gap;

            foreach (var line in block.Lines)
            {
                if (y + line.Height > bottom && y > top)
                {
                    pageIndex++;
                    y = top;
                }
                placements.Add(new Placement(pageIndex, y, page.MarginLeft + block.Indent, line));
                y += line.Height;
            }
        }
        return placements;
    }

    public static byte[] Render(PageSpec page, IReadOnlyList<FlowBlock> blocks, PdfInfo info, string? footerText = null)
    {
        var placements = Layout(page, blocks);
        var pageCount = placements.Count == 0 ? 1 : placements.Max(p => p.Page) + 1;
        var footerStyle = TextStyle.Regular(FooterSize, new SKColor(0x55, 0x55, 0x55));

        using var stream = new MemoryStream();
        // Start from Default: a bare initializer leaves EncodingQuality at 0,
        // which JPEG-compresses images at the lowest quality. 101 is lossless.
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = info.Title;
        metadata.Subject = info.Subject ?? string.Empty;
        metadata.Creator = info.Creator;
        metadata.Producer = "Cloud Health Office";
        metadata.Creation = info.CreatedAt;
        metadata.Modified = info.CreatedAt;
        metadata.RasterDpi = 300;
        metadata.EncodingQuality = 101;

        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            for (var p = 0; p < pageCount; p++)
            {
                var canvas = document.BeginPage(page.Width, page.Height);
                canvas.Clear(SKColors.White);
                foreach (var placement in placements.Where(x => x.Page == p))
                    placement.Line.Draw(canvas, placement.Left, placement.Top);

                var footer = $"Page {p + 1} of {pageCount}";
                if (!string.IsNullOrWhiteSpace(footerText)) footer = footerText + "    " + footer;
                foreach (var line in TextLayout.Wrap([new TextRun(footer, footerStyle)], page.ContentWidth))
                {
                    line.Draw(canvas, page.MarginLeft, page.Height - page.MarginBottom - line.Height);
                    break;
                }
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }
}
