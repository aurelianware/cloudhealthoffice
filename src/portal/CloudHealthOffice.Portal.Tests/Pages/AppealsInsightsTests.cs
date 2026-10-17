using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Appeals &amp; Grievances Insights: timeliness and outcomes by level, the
/// open queue, external review and appealed denial reasons, every number
/// linked to its cases; and the Appeals list using the same definitions.
/// </summary>
public class AppealsInsightsTests : TestContext
{
    private static readonly DateTime AsOf = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IAppealsService> _appeals = new();
    private const string Period = "from=2026-09-17T12%3A00%3A00Z&to=2026-10-17T12%3A00%3A00Z";

    public AppealsInsightsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_appeals.Object);
        var user = new Mock<IUserContextService>();
        user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "u1", Roles = ["UMCoordinator"] });
        user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);
        Services.AddSingleton(user.Object);
    }

    private static AppealInsightsView Sample() => new()
    {
        AsOf = AsOf, From = AsOf.AddDays(-30), To = AsOf,
        Received = new() { Total = 50, Appeals = 42, Grievances = 8, Expedited = 6, ExternalReview = 3 },
        ResolvedByLevel =
        [
            new() { Level = "FirstLevel", Standard = new() { Resolved = 20, OnTime = 18, Late = 2, Extended = 1, OnTimePercent = 90 }, Expedited = new() { Resolved = 4, OnTime = 4, OnTimePercent = 100 }, Overturned = 6, Upheld = 18, OverturnPercent = 25, ClosedWithoutDecision = 2 },
            new() { Level = "SecondLevel", Standard = new() { Resolved = 5, OnTime = 5, OnTimePercent = 100 }, Overturned = 1, Upheld = 4, OverturnPercent = 20 },
            new() { Level = "ExternalReview", Standard = new() { Resolved = 2, OnTime = 2, OnTimePercent = 100 }, Overturned = 1, Upheld = 1, OverturnPercent = 50 },
            new() { Level = "Grievance", Standard = new() { Resolved = 6, OnTime = 5, Late = 1, OnTimePercent = 83.3 } },
        ],
        Open = new() { Total = 14, Overdue = 2, DueWithin7Days = 5, PendingInfo = 3, Expedited = 1, ByLevel = [new() { Key = "FirstLevel", Count = 11 }, new() { Key = "Grievance", Count = 3 }] },
        AppealedDenialReasons = [new() { Code = "CO-50", Count = 12 }],
    };

    [Fact]
    public void Headline_figures_link_to_their_cases()
    {
        _appeals.Setup(a => a.GetAppealInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<AppealsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=stat-received]").TextContent.Should().Contain("50").And.Contain("8 grievances"));
        // 34 on time of 37 resolved across levels.
        cut.Find("[data-testid=stat-ontime]").TextContent.Should().Contain("91.9%").And.Contain("3 late of 37");
        cut.Find("[data-testid=stat-ontime-link]").GetAttribute("href").Should().Be($"/appeals?scope=resolved&{Period}&onTime=false");
        // Overturned of decided appeals only: 8 of 31 (grievances excluded).
        cut.Find("[data-testid=stat-overturned]").TextContent.Should().Contain("25.8%").And.Contain("8 of 31");
        cut.Find("[data-testid=stat-open]").TextContent.Should().Contain("2 overdue");
    }

    [Fact]
    public void The_level_table_drills_down_and_grievances_have_no_overturn()
    {
        _appeals.Setup(a => a.GetAppealInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<AppealsInsights>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=insights-level-row]").Should().HaveCount(4));
        cut.Find("[data-testid=level-FirstLevel-late]").GetAttribute("href").Should().Be($"/appeals?scope=resolved&{Period}&level=FirstLevel&onTime=false");
        cut.Find("[data-testid=level-FirstLevel-exp-ontime]").GetAttribute("href").Should().Be($"/appeals?scope=resolved&{Period}&level=FirstLevel&expedited=true&onTime=true");
        cut.Find("[data-testid=level-ExternalReview-overturned]").GetAttribute("href").Should().Be($"/appeals?scope=resolved&{Period}&level=ExternalReview&outcome=Overturned");
        cut.FindAll("[data-testid=level-Grievance-overturned]").Should().BeEmpty();
        cut.FindAll("[data-testid=insights-level-row]").Last().TextContent.Should().Contain("n/a");
        cut.Find("[data-testid=stat-external]").GetAttribute("href").Should().Be($"/appeals?scope=received&{Period}&externalReview=true");
        cut.Find("[data-testid=insights-reasons-link]").GetAttribute("href").Should().Be($"/appeals?scope=received&{Period}&kind=Appeal&denialReasonCode=CO-50");
    }

    [Fact]
    public void The_appeals_list_shows_the_queue_with_insights_definitions_and_opens_a_drill_down()
    {
        _appeals.Setup(a => a.GetAppealInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());
        IReadOnlyDictionary<string, string>? sent = null;
        _appeals.Setup(a => a.ListAppealInsightAsync(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback((IReadOnlyDictionary<string, string> q) => sent = q)
            .ReturnsAsync(new List<AppealSummary> { new() { Id = "rec-1", AppealId = "APL-1", Status = "InReview" } });
        Services.GetRequiredService<FakeNavigationManager>().NavigateTo("/appeals?scope=open&overdue=true");

        var cut = RenderComponent<Appeals>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("APL-1"));
        sent!["scope"].Should().Be("open");
        sent["overdue"].Should().Be("true");
        cut.Find("[data-testid=appeals-drilldown]").TextContent.Should().Contain("open now").And.Contain("overdue");
        cut.Find("[data-testid=appeals-overdue-link]").GetAttribute("href").Should().Be("/appeals?scope=open&overdue=true");
        cut.Find("[data-testid=appeals-overturned]").TextContent.Should().Contain("8 of 31");
        _appeals.Verify(a => a.GetSummaryAsync(), Times.Never);
    }
}
