using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Home and global search (docs/architecture/portal-navigation.md §3.1–3.2):
/// an identifier jumps straight to its record, anything else lists matches
/// grouped by kind, only modules the user may read are searched, and Home
/// shows my due work and what I opened recently.
/// </summary>
public class HomeAndSearchTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IMemberService> _members = new();
    private readonly Mock<IProviderService> _providers = new();
    private readonly Mock<IUserContextService> _user = new();
    private readonly Mock<IWorkManagementService> _work = new();
    private readonly Mock<IWorkspaceService> _workspace = new();

    public HomeAndSearchTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(_providers.Object);
        Services.AddSingleton(_user.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_workspace.Object);
        Services.AddScoped<GlobalSearch>();

        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>())).ReturnsAsync((ClaimDetails?)null);
        _members.Setup(m => m.SearchMembersAsync(It.IsAny<string>())).ReturnsAsync(new List<MemberSummary>());
        _providers.Setup(p => p.SearchProvidersAsync(It.IsAny<string>())).ReturnsAsync(new List<ProviderSummary>());
        _claims.Setup(c => c.GetRecentlyViewedClaimsAsync(It.IsAny<int>())).ReturnsAsync(new List<RecentClaimView>());
        _claims.Setup(c => c.GetMassAdjudicationRunsAsync(It.IsAny<int>())).ReturnsAsync(new List<MassAdjudicationRunSummary>());
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView>());
        _workspace.Setup(w => w.GetMyNoticesAsync()).ReturnsAsync(new List<PlanNoticeView>());
        _workspace.Setup(w => w.GetPinsAsync()).ReturnsAsync(new List<PinnedRecordView>());
    }

    private void Grant(params string[] permissions) =>
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => permissions.Contains(p));

    private GlobalSearch Search => Services.GetRequiredService<GlobalSearch>();

    [Fact]
    public async Task A_claim_number_is_an_exact_hit_to_jump_to()
    {
        Grant("claims:read", "members:read", "providers:read");
        _claims.Setup(c => c.GetClaimByIdAsync("CLM-77"))
            .ReturnsAsync(new ClaimDetails { ClaimId = "c77", ClaimNumber = "CLM-77", Status = "Paid", MemberId = "M1" });

        var results = await Search.SearchAsync(" CLM-77 ");

        results.Jump!.Href.Should().Be("/claims/c77");
    }

    [Fact]
    public async Task A_member_id_jumps_to_the_member_and_names_list_matches()
    {
        Grant("members:read");
        _members.Setup(m => m.GetMemberByIdAsync("M1")).ReturnsAsync(new MemberDetails { MemberId = "M1", FirstName = "Ana", LastName = "Reyes" });

        (await Search.SearchAsync("M1")).Jump!.Href.Should().Be("/members/M1");

        _members.Setup(m => m.SearchMembersAsync("Reyes")).ReturnsAsync(new List<MemberSummary>
        {
            new() { MemberId = "M1", FirstName = "Ana", LastName = "Reyes" },
            new() { MemberId = "M2", FirstName = "Luis", LastName = "Reyes" },
        });
        var byName = await Search.SearchAsync("Reyes");
        byName.Jump.Should().BeNull();
        byName.Hits.Select(h => h.Href).Should().Equal("/members/M1", "/members/M2");
    }

    [Fact]
    public async Task An_npi_is_an_exact_provider_hit()
    {
        Grant("providers:read");
        _providers.Setup(p => p.SearchProvidersAsync("1234567893")).ReturnsAsync(new List<ProviderSummary>
        {
            new() { ProviderId = "p1", NPI = "1234567893", Name = "Lakeside Orthopedics" },
        });

        (await Search.SearchAsync("1234567893")).Jump!.Href.Should().Be("/providers/p1");
    }

    [Fact]
    public async Task Only_modules_the_user_may_read_are_searched()
    {
        Grant("members:read");

        await Search.SearchAsync("anything");

        _claims.Verify(c => c.GetClaimByIdAsync(It.IsAny<string>()), Times.Never);
        _providers.Verify(p => p.SearchProvidersAsync(It.IsAny<string>()), Times.Never);
        _members.Verify(m => m.SearchMembersAsync("anything"), Times.Once);
    }

    [Fact]
    public async Task Two_exact_hits_list_both_instead_of_jumping_and_an_outage_is_reported()
    {
        Grant("claims:read", "members:read", "providers:read");
        _claims.Setup(c => c.GetClaimByIdAsync("X1")).ReturnsAsync(new ClaimDetails { ClaimId = "x", ClaimNumber = "X1" });
        _members.Setup(m => m.GetMemberByIdAsync("X1")).ReturnsAsync(new MemberDetails { MemberId = "X1", FirstName = "A", LastName = "B" });
        _providers.Setup(p => p.SearchProvidersAsync(It.IsAny<string>())).ThrowsAsync(new ServiceUnavailableException("Provider Service", new Exception()));

        var results = await Search.SearchAsync("X1");

        results.Jump.Should().BeNull();
        results.Hits.Where(h => h.Exact).Select(h => h.Kind).Should().Equal("Claim", "Member");
        results.Unavailable.Should().Equal("Providers");
    }

    [Fact]
    public void The_search_page_opens_an_exact_match_in_place_of_itself()
    {
        Grant("claims:read");
        _claims.Setup(c => c.GetClaimByIdAsync("CLM-77")).ReturnsAsync(new ClaimDetails { ClaimId = "c77", ClaimNumber = "CLM-77" });
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("q", "CLM-77"));

        RenderComponent<Search>();

        nav.Uri.Should().EndWith("/claims/c77");
        nav.History.First().Options.ReplaceHistoryEntry.Should().BeTrue();
    }

    [Fact]
    public void Home_shows_my_due_work_by_deadline_and_my_recent_claims()
    {
        Grant("workqueue:work", "claims:read");
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView>
        {
            new() { Id = "w2", WorkType = "Claim", SourceNumber = "CLM-2", DueAt = DateTime.UtcNow.AddHours(-3), OpenUrl = "/claims/c2" },
            new() { Id = "w1", WorkType = "Claim", SourceNumber = "CLM-1", DueAt = DateTime.UtcNow.AddDays(-2), OpenUrl = "/claims/c1" },
            new() { Id = "w3", WorkType = "Claim", SourceNumber = "CLM-3", DueAt = DateTime.UtcNow.AddDays(5), OpenUrl = "/claims/c3" },
        });
        _claims.Setup(c => c.GetRecentlyViewedClaimsAsync(It.IsAny<int>())).ReturnsAsync(new List<RecentClaimView>
        {
            new() { ClaimId = "c9", ClaimNumber = "CLM-9", MemberId = "M1", ViewedAt = DateTime.UtcNow.AddMinutes(-5) },
        });

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=home-due-item]").Should().HaveCount(2));
        cut.FindAll("[data-testid=home-due-item]").Select(i => i.TextContent).First().Should().Contain("CLM-1");
        cut.Find("[data-testid=home-recent-item]").TextContent.Should().Contain("CLM-9").And.Contain("5m ago");
    }

    [Fact]
    public void Home_hides_work_and_claims_panels_without_those_permissions()
    {
        Grant();

        var cut = RenderComponent<Home>();

        cut.FindAll("[data-testid=home-due]").Should().BeEmpty();
        cut.FindAll("[data-testid=home-recent]").Should().BeEmpty();
        cut.Find("[data-testid=global-search]");
        _work.Verify(w => w.GetMineAsync(), Times.Never);
    }

    [Fact]
    public void Home_shows_unread_notices_first_and_marks_them_read()
    {
        Grant("workspace:use");
        _workspace.Setup(w => w.GetMyNoticesAsync()).ReturnsAsync(new List<PlanNoticeView>
        {
            new() { Id = "n1", Title = "Maintenance Saturday", Severity = "Important", StartsAt = DateTime.UtcNow, CreatedBy = "admin" },
            new() { Id = "n2", Title = "Old news", Read = true, StartsAt = DateTime.UtcNow, CreatedBy = "admin" },
        });

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=home-notice]").Should().ContainSingle());
        cut.Find("[data-testid=home-notice]").TextContent.Should().Contain("Maintenance Saturday");
        cut.Find("[data-testid=home-notices-read]");

        cut.Find("[data-testid=notice-mark-read]").Click();

        _workspace.Verify(w => w.MarkNoticeReadAsync("n1"), Times.Once);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=home-notice]").Should().BeEmpty());
    }

    [Fact]
    public void Home_lists_my_pins_and_unpins()
    {
        Grant("workspace:use");
        _workspace.Setup(w => w.GetPinsAsync()).ReturnsAsync(new List<PinnedRecordView>
        {
            new() { Kind = "Member", RecordId = "M1", Label = "Ana Reyes" },
            new() { Kind = "Claim", RecordId = "c9", Label = "Claim CLM-9" },
        });

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=home-pin]").Should().HaveCount(2));
        cut.FindAll("[data-testid=home-pin] a.record-link-main").Select(a => a.GetAttribute("href")).Should().Equal("/members/M1", "/claims/c9");

        cut.FindAll("[data-testid=home-unpin]").First().Click();

        _workspace.Verify(w => w.UnpinAsync("Member", "M1"), Times.Once);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=home-pin]").Should().ContainSingle());
    }

    [Fact]
    public void A_record_can_be_pinned_and_unpinned_from_its_header()
    {
        Grant("workspace:use");
        var cut = RenderComponent<CloudHealthOffice.Portal.Shared.Records.PinButton>(p => p
            .Add(x => x.Kind, "Member").Add(x => x.RecordId, "M1").Add(x => x.Label, "Ana Reyes"));

        cut.Find("[data-testid=pin-button]").GetAttribute("aria-pressed").Should().Be("false");
        cut.Find("[data-testid=pin-button]").Click();

        _workspace.Verify(w => w.PinAsync("Member", "M1", "Ana Reyes"), Times.Once);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pin-button]").GetAttribute("aria-pressed").Should().Be("true"));
    }
}
