using IdCardService.Models;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;
using UglyToad.PdfPig;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// The ID card PDF, read back with PdfPig: what a member, a provider's front
/// desk or a document store actually gets.
/// </summary>
public class IdCardPdfTests
{
    // A6 landscape, 148 x 105 mm.
    private const double Width = 148 / 25.4 * 72;
    private const double Height = 105 / 25.4 * 72;
    private const double Margin = 16;

    private static readonly IdCardGenerator Generator = new(NullLogger<IdCardGenerator>.Instance);

    private static byte[] Qr(string payload)
    {
        using var data = new QRCodeGenerator().CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    private static CardBindings Bindings() => new()
    {
        MemberName = "José Núñez-Ødegaard",
        MemberNumber = "MBR-000123",
        GroupNumber = "GRP-77",
        SponsorName = "Synthetic Health Plan",
        PlanName = "Gold HMO",
        PlanId = "PLAN-GOLD",
        NetworkName = "Statewide Network",
        CopaySummary = "PCP $20 / Specialist $40 / ER $250",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CardId = "card-42",
        IssuedAt = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)
    };

    private static IdCardTemplate Template(params string[] disclaimers) => new()
    {
        Id = "t1",
        Name = "t",
        BackText = "Present this card at every visit.",
        Disclaimers = disclaimers.ToList()
    };

    private static async Task<PdfDocument> Render(IdCardTemplate template, CardBindings bindings)
    {
        var rendered = await Generator.RenderAsync(template, bindings, Qr("card-42"));
        return PdfDocument.Open(rendered.Pdf);
    }

    private static string Text(PdfDocument pdf) =>
        string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));

    [Fact]
    public async Task Card_IsOneA6LandscapePage_WithEveryField()
    {
        using var pdf = await Render(Template("Not a guarantee of coverage."), Bindings());

        Assert.Equal(1, pdf.NumberOfPages);
        var page = pdf.GetPage(1);
        Assert.Equal(Width, page.Width, 1);
        Assert.Equal(Height, page.Height, 1);

        var text = Text(pdf);
        foreach (var expected in new[]
        {
            "Synthetic Health Plan", "Gold HMO", "Member: José Núñez-Ødegaard", "Member ID: MBR-000123",
            "Group: GRP-77", "Plan: PLAN-GOLD", "Network: Statewide Network",
            "Copays: PCP $20 / Specialist $40 / ER $250", "Effective: 2026-01-01",
            "Present this card at every visit.", "Not a guarantee of coverage.", "Card card-42 • Issued 2026-10-21"
        })
            Assert.Contains(expected, text);
    }

    [Fact]
    public async Task OptionalFields_AreOmittedWhenEmpty()
    {
        var bindings = Bindings();
        bindings.GroupNumber = null;
        bindings.NetworkName = null;
        bindings.EffectiveDate = null;

        using var pdf = await Render(Template(), bindings);

        var text = Text(pdf);
        Assert.DoesNotContain("Group:", text);
        Assert.DoesNotContain("Network:", text);
        Assert.DoesNotContain("Effective:", text);
        Assert.Contains("Member ID: MBR-000123", text);
    }

    [Fact]
    public async Task Text_UsesTheEmbeddedNotoSansFonts()
    {
        using var pdf = await Render(Template(), Bindings());

        var fonts = pdf.GetPage(1).Letters.Select(l => l.FontName).Distinct().ToList();
        Assert.NotEmpty(fonts);
        Assert.All(fonts, f => Assert.Contains("NotoSans", f ?? string.Empty));
        Assert.Contains(fonts, f => f != null && f.Contains("SemiBold"));
    }

    [Fact]
    public async Task QrCode_IsEmbeddedInTheHeader()
    {
        using var pdf = await Render(Template(), Bindings());

        var image = Assert.Single(pdf.GetPage(1).GetImages());
        Assert.Equal(80, image.BoundingBox.Width, 1);
        Assert.Equal(80, image.BoundingBox.Height, 1);
        Assert.Equal(Width - Margin, image.BoundingBox.Right, 1);
        Assert.Equal(Height - Margin, image.BoundingBox.Top, 1);
    }

    [Fact]
    public async Task QrCode_IsEmbeddedLosslessly_SoItStaysScannable()
    {
        var rendered = await Generator.RenderAsync(Template(), Bindings(), Qr("card-42"));

        // A lossy (JPEG) QR image blurs module edges and can fail to scan.
        var raw = System.Text.Encoding.Latin1.GetString(rendered.Pdf);
        Assert.DoesNotContain("/DCTDecode", raw);
        using var pdf = PdfDocument.Open(rendered.Pdf);
        var image = Assert.Single(pdf.GetPage(1).GetImages());
        Assert.True(image.TryGetPng(out var png), "QR image should decode losslessly");
        Assert.NotEmpty(png);
    }

    [Fact]
    public async Task LongContent_WrapsInsideTheMargins_AndNeverOverlaps()
    {
        var bindings = Bindings();
        bindings.CopaySummary = string.Join(" / ", Enumerable.Range(1, 30).Select(i => $"Service {i} ${i * 5}"));
        bindings.MemberName = new string('X', 120);
        var disclaimers = Enumerable.Range(1, 12)
            .Select(i => $"Disclaimer {i}: coverage is subject to the terms, limits and exclusions of the member's plan documents.")
            .ToArray();

        using var pdf = await Render(Template(disclaimers), bindings);

        Assert.True(pdf.NumberOfPages >= 1);
        foreach (var page in pdf.GetPages())
        {
            Assert.All(page.Letters, l =>
            {
                Assert.InRange(l.BoundingBox.Left, Margin - 0.5, Width - Margin + 0.5);
                Assert.InRange(l.BoundingBox.Right, Margin - 0.5, Width - Margin + 0.5);
                Assert.InRange(l.BoundingBox.Bottom, Margin - 3, Height - Margin + 0.5);
            });
            // No two words share space on the page.
            var words = page.GetWords().Where(w => w.Text.Length > 0).ToList();
            for (var i = 0; i < words.Count; i++)
                for (var j = i + 1; j < words.Count; j++)
                {
                    var a = words[i].BoundingBox; var b = words[j].BoundingBox;
                    var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    var overlapY = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
                    Assert.False(overlapX > 0.5 && overlapY > 0.5, $"'{words[i].Text}' overlaps '{words[j].Text}'");
                }
        }

        var text = Text(pdf).Replace("\n", " ");
        Assert.Contains("Service 30 $150", text);
        Assert.Contains("Disclaimer 12:", text);
        Assert.Contains("Card card-42 • Issued 2026-10-21", text);
    }

    [Fact]
    public async Task SameInput_ProducesIdenticalPdf()
    {
        var first = await Generator.RenderAsync(Template("x"), Bindings(), Qr("card-42"));
        var second = await Generator.RenderAsync(Template("x"), Bindings(), Qr("card-42"));

        Assert.Equal(first.Pdf, second.Pdf);
    }

    [Fact]
    public async Task UndecodableQrImage_FailsInsteadOfIssuingACardWithoutIt()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generator.RenderAsync(Template(), Bindings(), new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void IdCardService_HasNoQuestPdfDependency()
    {
        var references = typeof(IdCardGenerator).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain(references, n => n != null && n.StartsWith("QuestPDF", StringComparison.OrdinalIgnoreCase));
    }
}
