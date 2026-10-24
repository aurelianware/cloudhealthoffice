using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using QRCoder;
using UglyToad.PdfPig;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// The sponsor policy a member is enrolled under, on their card
/// (plan-configuration.md §9b, §12.12).
///
/// <para>
/// A §9b policy is a group plus a policy number pointing at a benefit plan, and
/// enrolment now records which one a member joined. A provider keys eligibility
/// off the card, so where a member holds one of several policies of the same
/// employer, the card is where that has to be legible.
/// </para>
///
/// <para>
/// The property these pin is that a card for a member enrolled by group number
/// — every card until a tenant enters policies — is laid out exactly as it is
/// today. The field is conditional, like Group and Plan beside it, so it closes
/// up rather than printing an empty label.
/// </para>
/// </summary>
public class SponsorPolicyOnCardTests
{
    private static readonly IdCardGenerator Generator = new(NullLogger<IdCardGenerator>.Instance);

    private static CardBindings Bindings(string? sponsorPolicyNumber) => new()
    {
        MemberName = "Jane Doe",
        MemberNumber = "MBR-000123",
        GroupNumber = "GRP-77",
        SponsorPolicyNumber = sponsorPolicyNumber,
        SponsorName = "Synthetic Employer Group",
        PlanName = "Gold HMO",
        PlanId = "PLAN-GOLD",
        CardId = "card-42",
        IssuedAt = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc),
    };

    private static IdCardTemplate Template() => new() { Id = "t1", Name = "t" };

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
    public async Task A_member_on_a_policy_gets_both_the_group_and_the_policy()
    {
        // Both, not one or the other: the group is who the employer is and the
        // policy is which of its offerings this member joined. A provider
        // phoning about eligibility needs to be able to say which.
        var text = await PdfText(Bindings("GRP-77-0002"));

        Assert.Contains("Group: GRP-77", text);
        Assert.Contains("Policy: GRP-77-0002", text);
    }

    [Fact]
    public async Task A_member_enrolled_by_group_number_gets_the_card_exactly_as_before()
    {
        var text = await PdfText(Bindings(sponsorPolicyNumber: null));

        Assert.Contains("Group: GRP-77", text);
        // Not an empty label: the field closes up the way Plan and Network do.
        Assert.DoesNotContain("Policy:", text);
    }

    [Fact]
    public async Task A_blank_policy_number_is_the_same_as_none()
    {
        // Enrolment writes null, but a hand-edited record or an older import
        // could leave an empty string, and a label with nothing after it reads
        // as a card printed wrong.
        Assert.DoesNotContain("Policy:", await PdfText(Bindings("")));
    }

    [Fact]
    public void A_tenants_own_layout_can_place_the_policy_number()
    {
        const string svg = "<svg><text>{{GroupNumber}} / {{SponsorPolicyNumber}}</text></svg>";

        Assert.Contains("GRP-77 / GRP-77-0002",
            IdCardGenerator.SubstituteTokens(svg, Bindings("GRP-77-0002")));
    }

    [Fact]
    public async Task The_policy_reaches_the_card_from_the_coverage_record()
    {
        // The seam that makes the rest of this worth anything: the number is a
        // fact about the coverage, read where the group number is already read,
        // rather than a second lookup against the sponsor.
        var member = Substitute.For<IMemberClient>();
        var coverage = Substitute.For<ICoverageClient>();
        var sponsor = Substitute.For<ISponsorClient>();
        var plans = Substitute.For<IBenefitPlanClient>();
        var documents = Substitute.For<IMemberDocumentClient>();
        var generator = Substitute.For<IIdCardGenerator>();
        var templates = new InMemoryIdCardTemplateRepository();
        await templates.UpsertAsync(TestFixtures.GlobalDefault());

        member.GetAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId, FirstName = "Jane", LastName = "Doe" });
        coverage.GetActiveAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new CoverageDto
            {
                GroupNumber = TestFixtures.GroupNumber,
                SponsorPolicyNumber = "GRP-77-0002",
                PlanId = TestFixtures.PlanId,
                Status = 1,
            });
        generator.RenderAsync(Arg.Any<IdCardTemplate>(), Arg.Any<CardBindings>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedCard { Pdf = [0x25, 0x50, 0x44, 0x46], Png = [0x89, 0x50] });
        documents.UploadPdfAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("doc-pdf-1");
        documents.UploadPngAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("doc-png-1");

        var adapter = new ChoIdCardAdapter(
            member, coverage, sponsor, plans,
            new TemplateResolver(templates, NullLogger<TemplateResolver>.Instance),
            TestFixtures.QrService(), generator, documents,
            NullLogger<ChoIdCardAdapter>.Instance);

        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId,
            Channel = IdCardDeliveryChannel.Digital,
        });

        Assert.True(result.Success);
        await generator.Received(1).RenderAsync(
            Arg.Any<IdCardTemplate>(),
            Arg.Is<CardBindings>(b => b.SponsorPolicyNumber == "GRP-77-0002" && b.GroupNumber == TestFixtures.GroupNumber),
            Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_layout_that_places_it_for_a_member_without_one_renders_an_empty_token()
    {
        // The renderer's standing rule for every missing token, which is why
        // the built-in layout does not place this one: an SVG draws text at
        // fixed coordinates and cannot close the gap.
        const string svg = "<svg><text>{{GroupNumber}} / {{SponsorPolicyNumber}}</text></svg>";

        Assert.Contains("GRP-77 / ",
            IdCardGenerator.SubstituteTokens(svg, Bindings(sponsorPolicyNumber: null)));
    }
}
