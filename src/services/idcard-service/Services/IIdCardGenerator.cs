using System.Globalization;
using System.Text;
using IdCardService.Models;
using SkiaSharp;

namespace IdCardService.Services;

public interface IIdCardGenerator
{
    /// <summary>
    /// Renders the template with the supplied bindings + QR PNG bytes and
    /// returns the PDF and a PNG preview.
    /// </summary>
    Task<RenderedCard> RenderAsync(IdCardTemplate template, CardBindings bindings, byte[] qrPng, CancellationToken ct = default);
}

public class RenderedCard
{
    public byte[] Pdf { get; set; } = Array.Empty<byte>();
    public byte[]? Png { get; set; }
}

/// <summary>
/// Performs token substitution on the template SVG, rasterizes it to a PNG via
/// SkiaSharp (Svg.Skia) for a preview image, and lays the card out in a PDF
/// with SkiaSharp's PDF backend (<see cref="IdCardPdfRenderer"/>) using the
/// same data and the embedded QR.
/// </summary>
public class IdCardGenerator : IIdCardGenerator
{
    private readonly ILogger<IdCardGenerator> _logger;

    public IdCardGenerator(ILogger<IdCardGenerator> logger)
    {
        _logger = logger;
    }

    public Task<RenderedCard> RenderAsync(IdCardTemplate template, CardBindings bindings, byte[] qrPng, CancellationToken ct = default)
    {
        var svg = SubstituteTokens(template.LayoutSvg, bindings);

        byte[]? png = null;
        try
        {
            png = RasterizeSvgToPng(svg, qrPng);
        }
        catch (Exception ex)
        {
            // Preview is best-effort — a broken SVG shouldn't fail issuance.
            _logger.LogWarning(ex, "PNG preview rasterization failed for template {TemplateId}", template.Id);
        }

        var pdf = IdCardPdfRenderer.Render(template, bindings, qrPng);

        return Task.FromResult(new RenderedCard { Pdf = pdf, Png = png });
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

    private static byte[] RasterizeSvgToPng(string svg, byte[] qrPng)
    {
        using var svgDoc = new Svg.Skia.SKSvg();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
        svgDoc.Load(ms);

        if (svgDoc.Picture == null)
        {
            throw new InvalidOperationException("SVG parse yielded no picture");
        }

        var bounds = svgDoc.Picture.CullRect;
        var width = Math.Max(320, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(200, (int)Math.Ceiling(bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawPicture(svgDoc.Picture);

        if (qrPng is { Length: > 0 })
        {
            using var qrImage = SKImage.FromEncodedData(qrPng);
            if (qrImage != null)
            {
                var qrSize = Math.Min(width, height) / 3f;
                canvas.DrawImage(qrImage, new SKRect(width - qrSize - 12, height - qrSize - 12, width - 12, height - 12));
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
