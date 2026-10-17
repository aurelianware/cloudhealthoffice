using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Authorization Insights: the CMS-0057 figures, the queue now and denial
/// reasons, every number linked to the authorizations it counts; and the
/// authorizations list opening those links.
/// </summary>
public class AuthorizationsInsightsTests : TestContext
{
    private static readonly DateTime AsOf = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IAuthorizationService> _auths = new();

    public AuthorizationsInsightsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_auths.Object);
        var user = new Mock<IUserContextService>();
        user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "u1", Roles = ["UMCoordinator"] });
        user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);
        Services.AddSingleton(user.Object);
        Services.AddSingleton(Mock.Of<ISearchCriteriaPolicy>(p => p.RequiresCriteriaAsync() == Task.FromResult(true)));
    }

    private static AuthorizationInsightsView Sample() => new()
    {
        AsOf = AsOf, From = AsOf.AddDays(-30), To = AsOf,
        Reporting = new PriorAuthMetrics
        {
            TotalRequests = 120, DecidedRequests = 100, OpenRequests = 20, AutomatedDecisions = 40,
            Standard = new() { Requests = 90, Decided = 75, Approved = 60, Denied = 15, ApprovedPercent = 80, DeniedPercent = 20, DecidedWithinTimeframe = 70, DecidedWithinTimeframePercent = 93.3, MedianDecisionHours = 30 },
            Expedited = new() { Requests = 30, Decided = 25, Approved = 20, Denied = 5, ApprovedPercent = 80, DeniedPercent = 20, DecidedWithinTimeframe = 22, DecidedWithinTimeframePercent = 88, MedianDecisionHours = 20 },
            NotYetTracked = [new() { Metric = "Approved after appeal", Reason = "not linked yet" }],
        },
        Queue = new()
        {
            Total = 12, Expedited = 3,
            ByStatus = [new() { Key = "InReview", Count = 7 }, new() { Key = "Pended", Count = 5 }],
            BySla = [new() { Key = "None", Count = 8 }, new() { Key = "Warning", Count = 2 }, new() { Key = "Critical", Count = 1 }, new() { Key = "Breach", Count = 1 }],
        },
        DenialReasons = [new() { Code = "A1", Description = "Not medically necessary", Count = 14 }],
        Unused = new() { Ended = 40, WithoutClaim = 6 },
    };

    private const string Period = "from=2026-09-17T12%3A00%3A00Z&to=2026-10-17T12%3A00%3A00Z";

    [Fact]
    public void Headline_figures_link_to_their_authorizations()
    {
        _auths.Setup(a => a.GetAuthorizationInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<AuthorizationsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=stat-requests]").TextContent.Should().Contain("120"));
        cut.Find("[data-testid=stat-requests-link]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}");
        cut.Find("[data-testid=stat-approval]").TextContent.Should().Contain("80.0%").And.Contain("80 approved of 100");
        cut.Find("[data-testid=stat-expedited]").TextContent.Should().Contain("88.0%").And.Contain("3 late of 25");
        cut.Find("[data-testid=stat-expedited-link]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}&requestType=Expedited&withinTimeframe=false");
        cut.Find("[data-testid=stat-queue]").TextContent.Should().Contain("12").And.Contain("2 critical or past SLA");
        cut.Find("[data-testid=insights-gaps]").TextContent.Should().Contain("Approved after appeal");
    }

    [Fact]
    public void The_comparison_and_queue_rows_drill_down()
    {
        _auths.Setup(a => a.GetAuthorizationInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<AuthorizationsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=types-standard-late]"));
        cut.Find("[data-testid=types-standard-late]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}&requestType=Standard&withinTimeframe=false");
        cut.Find("[data-testid=types-expedited-denied]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}&requestType=Expedited&outcome=Denied");
        cut.Find("[data-testid=stat-automated]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}&automated=true");
        cut.FindAll("[data-testid=insights-sla-link]").Select(a => a.GetAttribute("href")).Should().Contain("/authorizations?scope=queue&sla=Breach");
        cut.Find("[data-testid=insights-sla]").TextContent.Should().Contain("Past SLA");
        cut.Find("[data-testid=insights-denials-link]").GetAttribute("href").Should().Be($"/authorizations?scope=period&{Period}&denialReasonCode=A1");
    }

    [Fact]
    public void The_list_opens_a_drill_down_and_says_what_it_shows()
    {
        IReadOnlyDictionary<string, string>? sent = null;
        _auths.Setup(a => a.ListAuthorizationInsightAsync(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback((IReadOnlyDictionary<string, string> q) => sent = q)
            .ReturnsAsync(new List<AuthorizationSummary> { new() { Id = "a1", AuthorizationNumber = "PA-1", Status = 6 } });
        Services.GetRequiredService<FakeNavigationManager>()
            .NavigateTo($"/authorizations?scope=period&{Period}&requestType=Expedited&withinTimeframe=false&lob=Medicaid");

        var cut = RenderComponent<Authorizations>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=authorization-link]").Should().ContainSingle());
        sent!["requestType"].Should().Be("Expedited");
        sent["withinTimeframe"].Should().Be("false");
        sent["lob"].Should().Be("Medicaid");
        cut.Find("[data-testid=authorizations-drilldown]").TextContent.Should().Contain("expedited").And.Contain("decided late").And.Contain("Medicaid");
        cut.Markup.Should().NotContain("Avg decision time");
        cut.Find("[data-testid=module-view-insights]").GetAttribute("href").Should().Be("/authorizations/insights");
    }

    [Fact]
    public void Approved_but_unused_links_to_its_authorizations()
    {
        _auths.Setup(a => a.GetAuthorizationInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<AuthorizationsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=stat-unused]"));
        cut.Find("[data-testid=stat-unused]").GetAttribute("href").Should().Be($"/authorizations?scope=unused&{Period}");
        cut.Find("[data-testid=insights-unused]").TextContent.Should().Contain("of 40 ended").And.Contain("15.0%");
    }

    [Fact]
    public void Approved_but_unused_says_why_when_claims_cant_be_checked()
    {
        var sample = Sample();
        sample.Unused = new() { Ended = 40, WithoutClaim = null, UnavailableReason = "Your role can't read claims, so claim usage can't be checked." };
        _auths.Setup(a => a.GetAuthorizationInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(sample);

        var cut = RenderComponent<AuthorizationsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=insights-unused-unavailable]").TextContent.Should().Contain("can't read claims"));
        cut.FindAll("[data-testid=stat-unused]").Should().BeEmpty();
    }
}
