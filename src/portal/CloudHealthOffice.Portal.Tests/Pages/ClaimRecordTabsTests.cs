using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The claim page as a record page (docs/architecture/portal-navigation.md):
/// every tab is a link with its own URL, the tab in the URL is honoured,
/// hidden tabs fall back to the default, and the Access log tab is shown
/// only with audit:read and loads only when opened.
/// </summary>
public class ClaimRecordTabsTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ClaimRecordTabsTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(_user.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClaimDetails { ClaimId = id, ClaimNumber = $"CLM-{id}", Status = "Paid" });
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
    }

    private void Grant(params string[] permissions) =>
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => permissions.Contains(p));

    private IRenderedComponent<ClaimDetailsNew> Render(string? tab = null, string? fromRun = null)
    {
        if (fromRun is not null)
        {
            var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            nav.NavigateTo(nav.GetUriWithQueryParameter("fromRun", fromRun));
        }
        var cut = RenderComponent<ClaimDetailsNew>(p =>
        {
            p.Add(x => x.ClaimId, "A");
            if (tab is not null) p.Add(x => x.Tab, tab);
        });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-A"));
        return cut;
    }

    [Fact]
    public void Tabs_are_links_to_their_own_urls_and_keep_the_query()
    {
        Grant();
        var cut = Render(fromRun: "run-9");

        var tabs = cut.FindAll("[data-testid^=record-tab-]");
        tabs.Select(t => t.GetAttribute("data-testid")).Should().Equal(
            "record-tab-summary", "record-tab-lines", "record-tab-adjudication", "record-tab-history", "record-tab-notes", "record-tab-documents");
        cut.Find("[data-testid=record-tab-lines]").GetAttribute("href").Should().Be("/claims/A/lines?fromRun=run-9");
    }

    [Fact]
    public void Opens_on_summary_and_honours_the_tab_in_the_url()
    {
        Grant();
        var cut = Render();
        cut.Find("[data-testid=record-tab-summary]").ClassList.Should().Contain("active");
        cut.FindAll("[data-testid=claim-tab-summary]").Should().ContainSingle();

        cut.SetParametersAndRender(p => p.Add(x => x.Tab, "history"));

        cut.Find("[data-testid=record-tab-history]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=claim-tab-summary]").Should().BeEmpty();
        cut.Find("[data-testid=claim-history-empty]");
    }

    [Fact]
    public void A_tab_the_user_may_not_see_falls_back_to_summary()
    {
        Grant();
        var cut = Render(tab: "access-log");

        cut.FindAll("[data-testid=record-tab-access-log]").Should().BeEmpty();
        cut.FindAll("[data-testid=claim-tab-summary]").Should().ContainSingle();
        _claims.Verify(c => c.GetClaimAccessLogAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Access_log_shows_who_read_the_claim_and_loads_only_when_opened()
    {
        Grant("audit:read");
        _claims.Setup(c => c.GetClaimAccessLogAsync("A", It.IsAny<int>())).ReturnsAsync(new List<ClaimAccessEntry>
        {
            new() { Action = "View", Outcome = "Allowed", ActorId = "examiner-7", ActorName = "Pat Examiner", ActorRoles = ["ClaimsExaminer"], OccurredAt = new DateTime(2026, 10, 15, 14, 3, 0) },
            new() { Action = "View", Outcome = "Denied", ActorId = "viewer-1", OccurredAt = new DateTime(2026, 10, 15, 13, 0, 0) },
        });

        var cut = Render();
        cut.Find("[data-testid=record-tab-access-log]");
        _claims.Verify(c => c.GetClaimAccessLogAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);

        cut.SetParametersAndRender(p => p.Add(x => x.Tab, "access-log"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=access-log-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=access-log-row]").First().TextContent.Should().Contain("Pat Examiner (examiner-7)").And.Contain("Viewed the claim");
        cut.FindAll("[data-testid=access-log-outcome]").Select(o => o.TextContent.Trim()).Should().Equal("Allowed", "Denied");
    }

    [Fact]
    public void The_header_opens_this_claim_on_this_tab_in_a_new_browser_tab()
    {
        Grant();
        var cut = Render(tab: "lines");

        var button = cut.Find("[data-testid=open-in-new-tab]");
        button.GetAttribute("href").Should().Be("/claims/A/lines");
        button.GetAttribute("target").Should().Be("_blank");
    }

    [Theory]
    [InlineData(null, "summary")]
    [InlineData("LINES", "lines")]
    [InlineData("nope", "summary")]
    [InlineData("hidden", "summary")]
    public void Resolve_picks_a_visible_tab_or_the_default(string? requested, string expected)
    {
        var tabs = new[] { new RecordTab("summary", "Summary"), new RecordTab("lines", "Lines"), new RecordTab("hidden", "Hidden", Visible: false) };
        RecordTabs.Resolve(tabs, requested, "summary").Should().Be(expected);
    }

    [Fact]
    public void A_record_link_also_offers_a_new_browser_tab()
    {
        var cut = RenderComponent<RecordLink>(p => p
            .Add(x => x.Href, "/claims/B")
            .AddUnmatched("data-testid", "duplicate-link")
            .AddChildContent("CLM-B"));

        cut.Find("[data-testid=duplicate-link]").GetAttribute("href").Should().Be("/claims/B");
        var newTab = cut.Find("[data-testid=record-link-new-tab]");
        newTab.GetAttribute("href").Should().Be("/claims/B");
        newTab.GetAttribute("target").Should().Be("_blank");
    }
}
