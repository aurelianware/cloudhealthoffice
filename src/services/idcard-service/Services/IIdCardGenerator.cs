using System.Globalization;
using System.Text;
using IdCardService.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IdCardService.Services;

public interface IIdCardGenerator
{
    /// <summary>
    /// Renders the template with the supplied bindings + QR PNG bytes and
    /// returns the PDF. A PNG preview is reserved for Phase 2 (Wallet /
    /// thumbnail rendering) and is null in Phase 1.
    /// </summary>
    Task<RenderedCard> RenderAsync(IdCardTemplate template, CardBindings bindings, byte[] qrPng, CancellationToken ct = default);
}

public class RenderedCard
{
    public byte[] Pdf { get; set; } = Array.Empty<byte>();

    /// <summary>Reserved for the Phase-2 PNG preview / Wallet thumbnail pipeline. Null in Phase 1.</summary>
    public byte[]? Png { get; set; }
}

/// <summary>
/// Performs token substitution on the template SVG (for audit / future
/// preview rendering) and lays out the card as a PDF via QuestPDF with the
/// bound data and the embedded QR image. PNG preview rasterization is
/// deferred to Phase 2 when the Wallet / thumbnail pipeline is specified.
/// </summary>
public class IdCardGenerator : IIdCardGenerator
{
    private readonly ILogger<IdCardGenerator> _logger;

    public IdCardGenerator(ILogger<IdCardGenerator> logger)
    {
        _logger = logger;

        // QuestPDF community license — no revenue threshold check here; the
        // product is responsible for ensuring the license is appropriate.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<RenderedCard> RenderAsync(IdCardTemplate template, CardBindings bindings, byte[] qrPng, CancellationToken ct = default)
    {
        // Exercise the token substitution now so upstream observability can
        // tell the difference between a missing token and a runtime render
        // bug. The substituted SVG isn't persisted in Phase 1; it'll be the
        // input to the PNG rasterizer in Phase 2.
        _ = SubstituteTokens(template.LayoutSvg, bindings);

        var pdf = BuildPdf(template, bindings, qrPng);
        return Task.FromResult(new RenderedCard { Pdf = pdf, Png = null });
    }

    public static string SubstituteTokens(string svg, CardBindings b)
    {
        if (string.IsNullOrEmpty(svg))
        {
            svg = DefaultLayoutSvg;
        }

        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MemberName"] = b.MemberName,
            ["MemberId"] = b.MemberId,
            ["MemberNumber"] = b.MemberNumber,
            ["DateOfBirth"] = b.DateOfBirth?.ToString("yyyy-MM-dd"),
            ["Gender"] = b.Gender,
            ["GroupNumber"] = b.GroupNumber,
            ["SponsorName"] = b.SponsorName,
            ["SponsorSupportPhone"] = b.SponsorSupportPhone,
            ["PlanId"] = b.PlanId,
            ["PlanName"] = b.PlanName,
            ["NetworkName"] = b.NetworkName,
            ["CoverageLevel"] = b.CoverageLevel,
            ["EffectiveDate"] = b.EffectiveDate?.ToString("yyyy-MM-dd"),
            ["TerminationDate"] = b.TerminationDate?.ToString("yyyy-MM-dd"),
            ["PcpName"] = b.PcpName,
            ["PcpPhone"] = b.PcpPhone,
            ["CopaySummary"] = b.CopaySummary,
            ["CardId"] = b.CardId,
            ["IssuedAt"] = b.IssuedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["LanguageCode"] = b.LanguageCode
        };

        var sb = new StringBuilder(svg);
        foreach (var kv in map)
        {
            sb.Replace("{{" + kv.Key + "}}", EscapeXml(kv.Value ?? string.Empty));
        }
        return sb.ToString();
    }

    // PNG preview rasterization intentionally omitted in Phase 1. Phase 2
    // will reintroduce SVG → PNG rendering when the Wallet + thumbnail
    // pipeline is specified; for now the PDF produced by QuestPDF is the
    // sole rendered deliverable.
    private static byte[]? RasterizeSvgToPng(string svg, byte[] qrPng) => null;

    private static byte[] BuildPdf(IdCardTemplate template, CardBindings b, byte[] qrPng)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A6.Landscape());
                page.Margin(16);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontSize(10));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(b.SponsorName ?? "Member ID Card").SemiBold().FontSize(14);
                        col.Item().Text(b.PlanName ?? string.Empty).FontSize(10);
                    });
                    row.ConstantItem(80).Height(80).Image(qrPng);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(4);
                    col.Item().Text(text =>
                    {
                        text.Span("Member: ").SemiBold();
                        text.Span(b.MemberName);
                    });
                    col.Item().Text(text =>
                    {
                        text.Span("Member ID: ").SemiBold();
                        text.Span(b.MemberNumber);
                    });
                    if (!string.IsNullOrEmpty(b.GroupNumber))
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Group: ").SemiBold();
                            text.Span(b.GroupNumber);
                        });
                    }
                    if (!string.IsNullOrEmpty(b.PlanId))
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Plan: ").SemiBold();
                            text.Span(b.PlanId!);
                        });
                    }
                    if (!string.IsNullOrEmpty(b.NetworkName))
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Network: ").SemiBold();
                            text.Span(b.NetworkName!);
                        });
                    }
                    if (!string.IsNullOrEmpty(b.CopaySummary))
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Copays: ").SemiBold();
                            text.Span(b.CopaySummary!);
                        });
                    }
                    if (b.EffectiveDate.HasValue)
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Effective: ").SemiBold();
                            text.Span(b.EffectiveDate.Value.ToString("yyyy-MM-dd"));
                        });
                    }
                });

                page.Footer().Column(col =>
                {
                    col.Spacing(2);
                    if (!string.IsNullOrEmpty(template.BackText))
                    {
                        col.Item().Text(template.BackText).FontSize(7);
                    }
                    foreach (var d in template.Disclaimers)
                    {
                        col.Item().Text(d).FontSize(7);
                    }
                    col.Item().Text($"Card {b.CardId} • Issued {b.IssuedAt:yyyy-MM-dd}").FontSize(7).FontColor(Colors.Grey.Darken2);
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static string EscapeXml(string s) => System.Security.SecurityElement.Escape(s) ?? string.Empty;

    private const string DefaultLayoutSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="600" height="360" viewBox="0 0 600 360">
          <rect width="600" height="360" fill="#ffffff" stroke="#dddddd"/>
          <text x="24" y="44" font-size="22" fill="#111111" font-family="Arial">{{SponsorName}}</text>
          <text x="24" y="72" font-size="14" fill="#555555" font-family="Arial">{{PlanName}}</text>
          <text x="24" y="140" font-size="16" fill="#111111" font-family="Arial">Member: {{MemberName}}</text>
          <text x="24" y="168" font-size="14" fill="#333333" font-family="Arial">ID: {{MemberNumber}}</text>
          <text x="24" y="196" font-size="14" fill="#333333" font-family="Arial">Group: {{GroupNumber}}</text>
          <text x="24" y="224" font-size="14" fill="#333333" font-family="Arial">Plan: {{PlanId}}</text>
          <text x="24" y="252" font-size="12" fill="#666666" font-family="Arial">Copays: {{CopaySummary}}</text>
          <text x="24" y="336" font-size="10" fill="#999999" font-family="Arial">Card {{CardId}} • Issued {{IssuedAt}}</text>
        </svg>
        """;
}
