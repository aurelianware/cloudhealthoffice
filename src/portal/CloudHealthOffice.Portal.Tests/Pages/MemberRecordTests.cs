using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The member record page (/members/{id}/{tab}) that replaces the member
/// dialog, and the member peek a claim opens instead
/// (docs/architecture/portal-navigation.md).
/// </summary>
public class MemberRecordTests : TestContext
{
    private readonly Mock<IMemberService> _members = new();
    private readonly Mock<IMemberNoteService> _notes = new();
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IRecordAccessLogService> _accessLog = new();

    public MemberRecordTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(_notes.Object);
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<ICoverageService>(c => c.GetCoverageByMemberIdAsync(It.IsAny<string>()) == Task.FromResult(new List<Coverage>())));
        Services.AddSingleton(Mock.Of<IBenefitPlanService>());
        Services.AddSingleton(Mock.Of<IFamilyRelationshipService>());
        Services.AddSingleton(Mock.Of<IMemberDocumentService>());
        Services.AddSingleton(Mock.Of<IUserContextService>());
        Services.AddSingleton(Mock.Of<IIdCardService>());
        Services.AddSingleton(Mock.Of<IArService>());
        Services.AddSingleton(Mock.Of<IPremiumBillingService>());
        Services.AddSingleton(Mock.Of<ISponsorService>());
        Services.AddSingleton(Mock.Of<IMemberAlertService>(a => a.ListAsync(It.IsAny<string>(), It.IsAny<bool>()) == Task.FromResult(new List<MemberAlertView>())));
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(_accessLog.Object);

        _members.Setup(m => m.GetMemberByIdAsync("M1")).ReturnsAsync(new MemberDetails
        {
            MemberId = "M1", FirstName = "Ana", LastName = "Reyes", DateOfBirth = new DateTime(1980, 2, 3), Status = "Active",
        });
        _notes.Setup(n => n.ListAsync(It.IsAny<string>(), It.IsAny<MemberNoteFilter>())).ReturnsAsync(new MemberNotePage());
    }

    private IRenderedComponent<MemberRecordView> Render(string? tab = null) =>
        RenderComponent<MemberRecordView>(p => p
            .Add(x => x.MemberId, "M1")
            .Add(x => x.Tab, tab)
            .Add(x => x.BaseHref, "/members/M1"));

    [Fact]
    public void Opens_on_overview_with_every_tab_as_a_link()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ana Reyes"));
        cut.FindAll("[data-testid^=record-tab-]").Select(t => t.GetAttribute("href")).Should().Equal(
            "/members/M1/overview", "/members/M1/enrollment", "/members/M1/coverage", "/members/M1/pcp",
            "/members/M1/family", "/members/M1/claims", "/members/M1/id-cards", "/members/M1/premium",
            "/members/M1/notes", "/members/M1/documents");
        cut.Find("[data-testid=record-tab-overview]").ClassList.Should().Contain("active");
        cut.Find("[data-testid=member-tab-overview]").TextContent.Should().Contain("Personal information");
        cut.Find("[data-testid=open-in-new-tab]").GetAttribute("href").Should().Be("/members/M1/overview");
    }

    [Fact]
    public void Notes_load_only_when_the_notes_tab_is_opened()
    {
        var cut = Render();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ana Reyes"));
        _notes.Verify(n => n.ListAsync(It.IsAny<string>(), It.IsAny<MemberNoteFilter>()), Times.Never);

        cut.SetParametersAndRender(p => p.Add(x => x.Tab, "notes"));

        cut.WaitForAssertion(() => _notes.Verify(n => n.ListAsync("M1", It.IsAny<MemberNoteFilter>()), Times.Once));
        cut.FindAll("[data-testid=member-tab-notes]").Should().ContainSingle();
        cut.FindAll("[data-testid=member-tab-overview]").Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_tab_falls_back_to_overview()
    {
        var cut = Render("nope");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=member-tab-overview]").Should().ContainSingle());
    }

    [Fact]
    public void Family_members_link_to_their_own_record()
    {
        var cut = RenderComponent<FamilyRelationshipTable>(p => p.Add(x => x.Rows, new List<FamilyRelationshipRow>
        {
            new() { Id = "r1", SubjectMemberId = "M1", RelatedMemberId = "M2", RelationshipCode = "19" },
        }));

        cut.Find("[data-testid=family-member-link]").GetAttribute("href").Should().Be("/members/M2/family");
        cut.Find("[data-testid=record-link-new-tab]").GetAttribute("target").Should().Be("_blank");
    }

    [Fact]
    public void A_claim_peeks_at_the_member_and_offers_open_and_new_tab()
    {
        _claims.Setup(c => c.GetClaimByIdAsync("C1")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "C1", ClaimNumber = "CLM-C1", Status = "Paid", MemberId = "M1",
        });
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
        var provider = RenderComponent<MudDialogProvider>();
        var claim = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));
        claim.WaitForAssertion(() => claim.Find("[data-testid=open-member]"));

        claim.Find("[data-testid=open-member]").Click();

        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Ana Reyes"));
        var newTab = provider.Find("[data-testid=member-peek-new-tab]");
        newTab.GetAttribute("href").Should().Be("/members/M1");
        newTab.GetAttribute("target").Should().Be("_blank");
        provider.Find("[data-testid=member-peek-open]");
    }

    [Fact]
    public void The_alert_banner_loads_once_per_member_even_when_the_host_re_renders()
    {
        var alerts = new Mock<IMemberAlertService>();
        alerts.Setup(a => a.ListAsync(It.IsAny<string>(), true)).ReturnsAsync(new List<MemberAlertView>());
        Services.AddSingleton(alerts.Object);
        var counts = 0;

        var cut = RenderComponent<MemberAlertBanner>(p => p
            .Add(x => x.MemberId, "M1")
            .Add(x => x.OnAlertCountChanged, (int _) => counts++));
        cut.SetParametersAndRender(p => p.Add(x => x.MemberId, "M1"));

        alerts.Verify(a => a.ListAsync("M1", true), Times.Once);
        counts.Should().Be(1);

        cut.SetParametersAndRender(p => p.Add(x => x.MemberId, "M2"));
        alerts.Verify(a => a.ListAsync("M2", true), Times.Once);
    }

    private void GrantAuditRead() =>
        Services.AddSingleton(Mock.Of<IUserContextService>(u => u.HasPermission("audit:read") == true));

    [Fact]
    public void The_access_log_tab_is_only_for_audit_readers_and_is_not_fetched_otherwise()
    {
        var cut = Render(tab: "access-log");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ana Reyes"));
        cut.FindAll("[data-testid=record-tab-access-log]").Should().BeEmpty();
        cut.FindAll("[data-testid=member-tab-overview]").Should().ContainSingle();
        _accessLog.Verify(a => a.GetMemberAccessLogAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void The_access_log_lists_reads_of_the_member_and_their_claims()
    {
        GrantAuditRead();
        _accessLog.Setup(a => a.GetMemberAccessLogAsync("M1", It.IsAny<int>())).ReturnsAsync(new RecordAccessLog(
        [
            new() { RecordType = "Claim", RecordId = "c1", Action = "View", Outcome = "Allowed", ActorId = "ex-1", OccurredAt = new DateTime(2026, 10, 2) },
            new() { RecordType = "Member", RecordId = "M1", Action = "View", Outcome = "Denied", ActorId = "fin-1", OccurredAt = new DateTime(2026, 10, 1) },
        ], []));

        var cut = Render(tab: "access-log");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=access-log-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=access-log-record] a.record-link-main").Select(a => a.GetAttribute("href")).Should().Equal("/claims/c1");
        cut.FindAll("[data-testid=access-log-outcome]").Select(o => o.TextContent).Should().Equal("Allowed", "Denied");
        cut.FindAll("[data-testid=access-log-partial]").Should().BeEmpty();
    }

    [Fact]
    public void A_source_that_cannot_be_read_is_named_rather_than_hiding_the_rest()
    {
        GrantAuditRead();
        _accessLog.Setup(a => a.GetMemberAccessLogAsync("M1", It.IsAny<int>())).ReturnsAsync(new RecordAccessLog(
            [new() { RecordType = "Member", RecordId = "M1", Action = "View", Outcome = "Allowed", ActorId = "csr-1" }],
            ["Claims Service"]));

        var cut = Render(tab: "access-log");

        cut.WaitForAssertion(() => cut.Find("[data-testid=access-log-partial]").TextContent.Should().Contain("Claims Service"));
        cut.FindAll("[data-testid=access-log-row]").Should().ContainSingle();
    }
}
