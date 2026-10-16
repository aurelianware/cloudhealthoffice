using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The provider record page (/providers/{id}/{tab}) that replaces the
/// provider dialog (docs/architecture/portal-navigation.md §4).
/// </summary>
public class ProviderRecordTests : TestContext
{
    private readonly Mock<IProviderService> _providers = new();
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ProviderRecordTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_providers.Object);
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_user.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);

        _providers.Setup(p => p.GetProviderByIdAsync("P1")).ReturnsAsync(new ProviderDetails
        {
            ProviderId = "P1", NPI = "1234567893", Name = "Lakeside Orthopedics", Specialty = "Orthopedics", NetworkStatus = "In-Network",
        });
        _claims.Setup(c => c.SearchClaimsAsync(It.Is<ClaimSearchRequest>(r => r.ProviderId == "1234567893")))
            .ReturnsAsync(new ClaimSearchResult
            {
                TotalCount = 1, PageSize = 25,
                Claims = [new ClaimSummary { ClaimId = "c1", ClaimNumber = "CLM-1", MemberId = "M1", Status = "Paid", TotalChargeAmount = 100m }],
            });
    }

    private IRenderedComponent<ProviderRecordView> Render(string? tab = null) =>
        RenderComponent<ProviderRecordView>(p => p.Add(x => x.ProviderId, "P1").Add(x => x.Tab, tab).Add(x => x.BaseHref, "/providers/P1"));

    [Fact]
    public void Opens_on_overview_with_every_tab_as_a_link()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Lakeside Orthopedics"));
        cut.FindAll("[data-testid^=record-tab-]").Select(t => t.GetAttribute("href")).Should().Equal(
            "/providers/P1/overview", "/providers/P1/locations", "/providers/P1/networks", "/providers/P1/credentials",
            "/providers/P1/contract", "/providers/P1/claims", "/providers/P1/performance");
        cut.FindAll("[data-testid=provider-tab-overview]").Should().ContainSingle();
        cut.Find("[data-testid=provider-edit]");
    }

    [Fact]
    public void Claims_load_by_npi_only_when_the_claims_tab_is_opened_and_link_to_claim_and_member()
    {
        var cut = Render();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Lakeside Orthopedics"));
        _claims.Verify(c => c.SearchClaimsAsync(It.IsAny<ClaimSearchRequest>()), Times.Never);

        cut.SetParametersAndRender(p => p.Add(x => x.Tab, "claims"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=provider-claim]").Should().ContainSingle());
        var hrefs = cut.FindAll("[data-testid=provider-claim] a.record-link-main").Select(a => a.GetAttribute("href")).ToList();
        hrefs.Should().Equal("/claims/c1", "/members/M1");
    }

    [Fact]
    public void Edit_is_hidden_without_providers_write()
    {
        _user.Setup(u => u.HasPermission("providers:write")).Returns(false);
        var cut = Render();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Lakeside Orthopedics"));
        cut.FindAll("[data-testid=provider-edit]").Should().BeEmpty();
    }

    [Fact]
    public void A_provider_that_cannot_be_loaded_says_so()
    {
        var cut = RenderComponent<ProviderRecordView>(p => p.Add(x => x.ProviderId, "nope").Add(x => x.BaseHref, "/providers/nope"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=provider-missing]"));
    }

    [Fact]
    public void The_claim_header_opens_the_billing_providers_record()
    {
        _claims.Setup(c => c.GetClaimByIdAsync("C1")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "C1", ClaimNumber = "CLM-C1", Status = "Paid", BillingProviderNPI = "1234567893", BillingProviderName = "Lakeside Orthopedics",
        });
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
        _providers.Setup(p => p.SearchProvidersAsync("1234567893"))
            .ReturnsAsync(new List<ProviderSummary> { new() { ProviderId = "P1", NPI = "1234567893", Name = "Lakeside Orthopedics" } });
        var claim = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));
        claim.WaitForAssertion(() => claim.Find("[data-testid=open-billing-provider]"));

        claim.Find("[data-testid=open-billing-provider]").Click();

        Services.GetRequiredService<FakeNavigationManager>().Uri.Should().EndWith("/providers/P1");
    }
}
