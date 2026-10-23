using CloudHealthOffice.PlanConfiguration;
using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;
using UglyToad.PdfPig;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// Who the card says issued the coverage
/// (plan-configuration.md §12.4).
///
/// <para>
/// A member ID card is conventionally headed by the health plan that issued the
/// coverage, with the employer group in the Group field. CHO headed it with the
/// sponsor instead, not as a design decision but because it had no issuer to
/// put there. The carrier supplies one, and the sponsor stays as the fallback,
/// so a tenant whose carrier states no letterhead name gets exactly the card it
/// got before.
/// </para>
/// </summary>
public class CarrierIssuerTests
{
    private static readonly IdCardGenerator Generator = new(NullLogger<IdCardGenerator>.Instance);

    private static readonly CarrierIdentity Acme = new()
    {
        Id = "tenant-1|HMO",
        Code = "HMO",
        Name = "the HMO",
        LegalName = "Acme Health Plan of Texas",
        CorrespondenceAddress = new CarrierAddress
        {
            Street = "100 Congress Ave", City = "Austin", State = "TX", ZipCode = "78701",
        },
        IsDefault = true,
    };

    private static CardBindings Bindings(string? issuer, string? issuerAddress = null, string? sponsor = "Synthetic Employer Group") => new()
    {
        MemberName = "Jane Doe",
        MemberNumber = "MBR-000123",
        GroupNumber = "GRP-77",
        SponsorName = sponsor,
        PlanName = "Gold HMO",
        PlanId = "PLAN-GOLD",
        IssuerName = issuer,
        IssuerAddress = issuerAddress,
        CardId = "card-42",
        IssuedAt = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
    };

    private static IdCardTemplate Template() => new() { Id = "t1", Name = "t", BackText = "Present this card at every visit." };

    private static byte[] Qr()
    {
        using var data = new QRCodeGenerator().CreateQrCode("card-42", QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    private static async Task<string> PdfText(CardBindings bindings)
    {
        var rendered = await Generator.RenderAsync(Template(), bindings, Qr());
        using var pdf = PdfDocument.Open(rendered.Pdf);
        return string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public async Task The_issuer_heads_the_card_and_the_employer_group_stays_in_the_group_field()
    {
        var text = await PdfText(Bindings("Acme Health Plan of Texas", "100 Congress Ave • Austin, TX 78701"));

        Assert.Contains("Acme Health Plan of Texas", text);
        Assert.Contains("Group: GRP-77", text);
        Assert.Contains("100 Congress Ave", text);
        // The employer does not head a card that names its issuer.
        Assert.DoesNotContain("Synthetic Employer Group", text);
    }

    [Fact]
    public async Task A_carrier_that_states_no_letterhead_leaves_the_card_exactly_as_it_was()
    {
        var text = await PdfText(Bindings(issuer: null));

        Assert.Contains("Synthetic Employer Group", text);
        Assert.Contains("Gold HMO", text);
        Assert.Contains("Group: GRP-77", text);
    }

    [Fact]
    public async Task A_card_with_neither_issuer_nor_sponsor_still_renders()
    {
        var text = await PdfText(Bindings(issuer: null, sponsor: null));

        Assert.Contains("Member ID Card", text);
        Assert.Contains("Jane Doe", text);
    }

    [Fact]
    public void The_svg_layout_has_tokens_for_the_issuer_and_falls_back_to_the_sponsor()
    {
        const string svg = "<svg><text>{{CardTitle}} | {{IssuerName}} | {{IssuerAddress}} | {{SponsorName}}</text></svg>";

        Assert.Contains(
            "Acme Health Plan of Texas | Acme Health Plan of Texas | 100 Congress Ave | Synthetic Employer Group",
            IdCardGenerator.SubstituteTokens(svg, Bindings("Acme Health Plan of Texas", "100 Congress Ave")));
        // CardTitle is what a layout heads the card with, so it is the one that falls back.
        Assert.Contains(
            "Synthetic Employer Group |  |  | Synthetic Employer Group",
            IdCardGenerator.SubstituteTokens(svg, Bindings(issuer: null)));
    }

    [Fact]
    public void A_carriers_address_becomes_one_line_in_the_order_it_is_read()
    {
        Assert.Equal("100 Congress Ave • Austin, TX 78701", ChoIdCardAdapter.IssuerAddressLine(Acme));
    }

    [Fact]
    public void An_address_the_carrier_states_only_in_part_drops_the_missing_pieces()
    {
        Assert.Equal("Austin, TX", ChoIdCardAdapter.IssuerAddressLine(Acme with
        {
            CorrespondenceAddress = new CarrierAddress { City = "Austin", State = "TX" },
        }));
        Assert.Equal("100 Congress Ave", ChoIdCardAdapter.IssuerAddressLine(Acme with
        {
            CorrespondenceAddress = new CarrierAddress { Street = "100 Congress Ave" },
        }));
    }

    [Fact]
    public void A_carrier_with_no_address_puts_no_blank_line_on_the_back_of_the_card()
    {
        Assert.Null(ChoIdCardAdapter.IssuerAddressLine(Acme with { CorrespondenceAddress = null }));
        Assert.Null(ChoIdCardAdapter.IssuerAddressLine(Acme with { CorrespondenceAddress = new CarrierAddress() }));
        Assert.Null(ChoIdCardAdapter.IssuerAddressLine(null));
    }
}
