using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The authorization record page (/authorizations/{id}/{tab}) that replaces
/// the authorization dialog: member and provider are links in the header, and
/// the claim's auth chip links to it.
/// </summary>
public class AuthorizationRecordTests : TestContext
{
    private readonly Mock<IAuthorizationService> _auths = new();
    private readonly Mock<IClaimsService> _claims = new();

    public AuthorizationRecordTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_auths.Object);
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IUserContextService>());
        Services.AddSingleton(Mock.Of<IWorkQueueService>());

        _auths.Setup(a => a.GetAuthorizationByIdAsync("PA-77")).ReturnsAsync(new AuthorizationDetails
        {
            AuthorizationNumber = "PA-77", MemberId = "M1", PatientFirstName = "Ana", PatientLastName = "Reyes",
            ProviderId = "1234567893", RequestingProviderName = "Lakeside Orthopedics",
            UnitsRequested = 10, UnitsApproved = 8, SubmittedDate = DateTime.UtcNow.AddDays(-2),
        });
    }

    private IRenderedComponent<AuthorizationRecordView> Render(string? tab = null) =>
        RenderComponent<AuthorizationRecordView>(p => p
            .Add(x => x.AuthorizationId, "PA-77").Add(x => x.Tab, tab).Add(x => x.BaseHref, "/authorizations/PA-77"));

    [Fact]
    public void Opens_on_overview_with_member_and_provider_as_links()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Authorization PA-77"));
        cut.FindAll("[data-testid^=record-tab-]").Select(t => t.GetAttribute("href")).Should().Equal(
            "/authorizations/PA-77/overview", "/authorizations/PA-77/services", "/authorizations/PA-77/review", "/authorizations/PA-77/documents");
        cut.FindAll("[data-testid=authorization-tab-overview]").Should().ContainSingle();
        cut.Find("[data-testid=authorization-member]").GetAttribute("href").Should().Be("/members/M1");
        cut.Find("[data-testid=authorization-member]").TextContent.Should().Be("Ana Reyes");
        cut.Find("[data-testid=authorization-provider]").GetAttribute("href").Should().Be("/providers/npi/1234567893");
    }

    [Fact]
    public void Honours_the_tab_in_the_url()
    {
        var cut = Render("review");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=authorization-tab-review]").Should().ContainSingle());
        cut.FindAll("[data-testid=authorization-tab-overview]").Should().BeEmpty();
    }

    [Fact]
    public void The_claims_auth_chip_links_to_the_authorization()
    {
        _claims.Setup(c => c.GetClaimByIdAsync("C1")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "C1", ClaimNumber = "CLM-C1", Status = "Paid", PriorAuthorizationNumber = "PA-77",
        });
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());

        var claim = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));

        claim.WaitForAssertion(() => claim.Find("[data-testid=open-authorization]"));
        claim.Find("[data-testid=open-authorization]").GetAttribute("href").Should().Be("/authorizations/PA-77");
    }

    private IRenderedComponent<AuthorizationRecordView> RenderAsClaimsReader(string tab)
    {
        Services.AddSingleton(Mock.Of<IUserContextService>(u => u.HasPermission("claims:read") == true));
        return Render(tab);
    }

    [Fact]
    public void Related_claims_are_only_for_claims_readers()
    {
        var cut = Render("claims");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Authorization PA-77"));
        cut.FindAll("[data-testid=record-tab-claims]").Should().BeEmpty();
        _claims.Verify(c => c.SearchClaimsAsync(It.IsAny<ClaimSearchRequest>()), Times.Never);
    }

    [Fact]
    public void Related_claims_lists_claims_billed_against_the_auth_number_with_totals()
    {
        _claims.Setup(c => c.SearchClaimsAsync(It.Is<ClaimSearchRequest>(r => r.AuthorizationNumber == "PA-77")))
            .ReturnsAsync(new ClaimSearchResult
            {
                TotalCount = 2, PageSize = 100,
                Claims =
                [
                    new ClaimSummary { ClaimId = "c1", ClaimNumber = "CLM-1", MemberId = "M1", Status = "Paid", TotalChargeAmount = 300m, PaidAmount = 210m },
                    new ClaimSummary { ClaimId = "c2", ClaimNumber = "CLM-2", MemberId = "M1", Status = "Pended", TotalChargeAmount = 150m },
                ],
            });

        var cut = RenderAsClaimsReader("claims");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=authorization-claim]").Should().HaveCount(2));
        cut.Find("[data-testid=record-tab-claims]").GetAttribute("href").Should().Be("/authorizations/PA-77/claims");
        cut.FindAll("[data-testid=authorization-claim] a.record-link-main").Select(a => a.GetAttribute("href"))
            .Should().Equal("/claims/c1", "/members/M1", "/claims/c2", "/members/M1");
        var summary = cut.Find("[data-testid=authorization-claims-summary]").TextContent;
        summary.Should().Contain("450.00").And.Contain("210.00").And.Contain("8");
    }

    [Fact]
    public void Related_claims_says_so_when_none_were_billed()
    {
        _claims.Setup(c => c.SearchClaimsAsync(It.IsAny<ClaimSearchRequest>())).ReturnsAsync(new ClaimSearchResult());

        var cut = RenderAsClaimsReader("claims");

        cut.WaitForAssertion(() => cut.Find("[data-testid=authorization-claims-empty]").TextContent.Should().Contain("PA-77"));
    }
}
