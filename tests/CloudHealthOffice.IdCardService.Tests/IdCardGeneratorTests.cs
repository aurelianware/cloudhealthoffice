using IdCardService.Models;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.IdCardService.Tests;

public class IdCardGeneratorTests
{
    [Fact]
    public void SubstituteTokens_ReplacesAllKnownTokens()
    {
        var svg = "<svg><text>{{MemberName}} / {{PlanId}} / {{CardId}} / {{IssuedAt}}</text></svg>";
        var bindings = new CardBindings
        {
            MemberName = "Jane Doe",
            PlanId = "PLAN-1",
            CardId = "card-xyz",
            IssuedAt = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc)
        };

        var result = IdCardGenerator.SubstituteTokens(svg, bindings);

        Assert.Contains("Jane Doe", result);
        Assert.Contains("PLAN-1", result);
        Assert.Contains("card-xyz", result);
        Assert.Contains("2026-04-20", result);
        Assert.DoesNotContain("{{", result);
    }

    [Fact]
    public void SubstituteTokens_UnknownTokenLeftInPlace()
    {
        var svg = "<svg><text>{{MemberName}} // {{Unknown}}</text></svg>";
        var result = IdCardGenerator.SubstituteTokens(svg, new CardBindings { MemberName = "X" });

        Assert.Contains("X", result);
        Assert.Contains("{{Unknown}}", result);
    }

    [Fact]
    public async Task Render_ProducesNonEmptyPdf()
    {
        var generator = new IdCardGenerator(NullLogger<IdCardGenerator>.Instance);

        var template = new IdCardTemplate
        {
            Id = "t1",
            Name = "t",
            LayoutSvg = string.Empty,
            BackText = "See reverse",
            Disclaimers = new() { "Not a guarantee of coverage" }
        };

        var bindings = new CardBindings
        {
            MemberName = "Jane Doe",
            MemberNumber = "MBR-1",
            PlanName = "Gold HMO",
            CardId = "card-1",
            IssuedAt = DateTime.UtcNow
        };

        // Minimal 1x1 PNG bytes (valid PNG header) for the QR image.
        var pngHeader = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82
        };

        var rendered = await generator.RenderAsync(template, bindings, pngHeader);

        Assert.NotNull(rendered.Pdf);
        Assert.True(rendered.Pdf.Length > 200, "PDF should be non-trivial");
        // Starts with %PDF- magic bytes
        Assert.Equal(0x25, rendered.Pdf[0]);
        Assert.Equal(0x50, rendered.Pdf[1]);
    }
}
