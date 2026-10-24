using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages.Compliance;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The CMS-0057-F console: open requests against their 72-hour / 7-day clocks,
/// recent decisions with who decided and why, and the public metrics, with the
/// metrics the data can't support yet named rather than shown as zero.
/// </summary>
public class Cms0057ConsolePageTests : TestContext
{
    private readonly Mock<ICms0057Service> _service = new();

    public Cms0057ConsolePageTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_service.Object);
        Services.AddSingleton(GatingUsers.With("authorizations:read").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _service.Setup(s => s.GetDecisionClocksAsync()).ReturnsAsync(new List<PriorAuthClock>
        {
            new() { Id = "a1", AuthorizationNumber = "PA-OK", MemberId = "M1", Status = "Submitted", LevelOfService = "E",
                    SlaDeadline = DateTime.UtcNow.AddDays(5), HoursRemaining = 120, PercentConsumed = 28, EscalationLevel = "None" },
            new() { Id = "a2", AuthorizationNumber = "PA-SOON", MemberId = "M2", Status = "InReview", LevelOfService = "U",
                    SlaDeadline = DateTime.UtcNow.AddHours(10), HoursRemaining = 10, PercentConsumed = 86, EscalationLevel = "Critical" },
            new() { Id = "a3", AuthorizationNumber = "PA-LATE", MemberId = "M3", Status = "Pended", LevelOfService = "U",
                    SlaDeadline = DateTime.UtcNow.AddHours(-5), HoursRemaining = -5, PercentConsumed = 107, EscalationLevel = "Breach" },
        });
        _service.Setup(s => s.GetRecentDecisionsAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<PriorAuthDecision>
        {
            new() { Id = "d1", AuthorizationNumber = "PA-AUTO", MemberId = "M4", RequestType = "Standard", Status = "Approved",
                    DecisionHours = 0.2, TimeframeHours = 168, WithinTimeframe = true, Automated = true,
                    DecidedBy = "automated:AutoApproveList;lob=Medicaid;state=TX", Reason = "On the auto-approve list" },
            new() { Id = "d2", AuthorizationNumber = "PA-DENY", MemberId = "M5", RequestType = "Expedited", Status = "Denied",
                    DecisionHours = 80, TimeframeHours = 72, WithinTimeframe = false, DecidedBy = "Dr. Reviewer",
                    Reason = "Clinical criteria not met", DenialReasonCode = "N350" },
        });
        _service.Setup(s => s.GetMetricsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(new PriorAuthMetrics
        {
            From = new DateTime(2026, 1, 1), To = new DateTime(2026, 10, 15),
            TotalRequests = 10, DecidedRequests = 8, AutomatedDecisions = 2,
            Standard = new() { TimeframeHours = 168, Requests = 6, Decided = 5, ApprovedPercent = 80, DeniedPercent = 20,
                               AverageDecisionHours = 30.5, MedianDecisionHours = 26, DecidedWithinTimeframe = 5, DecidedWithinTimeframePercent = 100 },
            Expedited = new() { TimeframeHours = 72, Requests = 4, Decided = 3, ApprovedPercent = 66.7, DeniedPercent = 33.3,
                                AverageDecisionHours = 50, MedianDecisionHours = 40, DecidedWithinTimeframe = 2, DecidedWithinTimeframePercent = 66.7 },
            NotYetTracked = { new() { Metric = "Approved after appeal", Reason = "Appeal outcomes are not yet linked." } },
        });
    }

    [Fact]
    public void Kpis_CountOpenDueSoonAndOverdue_AndOnTimeAcrossBothRequestTypes()
    {
        var cut = RenderComponent<Cms0057Console>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=kpi-open]").TextContent.Should().Contain("3"));
        cut.Find("[data-testid=kpi-at-risk]").TextContent.Should().Contain("1");
        cut.Find("[data-testid=kpi-overdue]").TextContent.Should().Contain("1");
        // (5 + 2) of (5 + 3) decided on time = 87.5%; 2 of 8 automated = 25%.
        cut.Find("[data-testid=kpi-on-time]").TextContent.Should().Contain("87.5%").And.Contain("25% automated");
    }

    [Fact]
    public void DecisionClocks_ListMostUrgentFirst_WithOverdueTime()
    {
        var cut = RenderComponent<Cms0057Console>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=clocks]").TextContent.Should().Contain("PA-LATE"));
        var text = cut.Find("[data-testid=clocks]").TextContent;
        text.IndexOf("PA-LATE", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("PA-SOON", StringComparison.Ordinal));
        text.IndexOf("PA-SOON", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("PA-OK", StringComparison.Ordinal));
        text.Should().Contain("Overdue by 5 h").And.Contain("10 h left").And.Contain("5 d left");
        text.Should().Contain("Expedited (72 h)").And.Contain("Standard (7 days)");
    }

    [Fact]
    public void RecentDecisions_ShowTheRuleOrReviewer_LateDecisions_AndReasons()
    {
        var cut = RenderComponent<Cms0057Console>();
        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").Should().HaveCountGreaterThan(1));

        cut.FindAll(".mud-tab").Single(t => t.TextContent.Contains("Recent decisions")).Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=decisions]").TextContent.Should().Contain("PA-AUTO"));
        var text = cut.Find("[data-testid=decisions]").TextContent;
        text.Should().Contain("Automated: AutoApproveList").And.NotContain("lob=Medicaid");
        text.Should().Contain("Dr. Reviewer").And.Contain("3 d 8 h of 72 h").And.Contain("(late)");
        text.Should().Contain("Clinical criteria not met").And.Contain("N350");
    }

    [Fact]
    public void PublicMetrics_ShowBothRequestTypes_AndNameWhatIsNotTrackedYet()
    {
        var cut = RenderComponent<Cms0057Console>();
        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").Should().HaveCountGreaterThan(2));

        cut.FindAll(".mud-tab").Single(t => t.TextContent.Contains("Public metrics")).Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=metrics]").TextContent.Should().Contain("66.7%"));
        var text = cut.Find("[data-testid=metrics]").TextContent;
        text.Should().Contain("80%").And.Contain("30.5 h").And.Contain("2 d 2 h");
        cut.Markup.Should().Contain("of all received requests").And.Contain("incomplete annual public report");
        cut.Find("[data-testid=not-tracked]").TextContent.Should().Contain("Approved after appeal");
    }

    [Fact]
    public void ServiceDown_ShowsAnErrorInsteadOfEmptyTables()
    {
        _service.Setup(s => s.GetDecisionClocksAsync())
            .ThrowsAsync(new ServiceUnavailableException("Authorization Service", new HttpRequestException()));

        var cut = RenderComponent<Cms0057Console>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=error]").TextContent.Should().Contain("unavailable"));
    }

    [Theory]
    [InlineData(3.24, "3.2 h")]
    [InlineData(47.9, "47.9 h")]
    [InlineData(48.0, "2 d")]
    [InlineData(80.0, "3 d 8 h")]
    [InlineData(null, "—")]
    public void Duration_ReadsAsHoursUnderTwoDays_ThenDays(double? hours, string expected) =>
        Cms0057Console.Duration(hours).Should().Be(expected);

    [Theory]
    [InlineData(72.0, "72 h")]
    [InlineData(168.0, "7 days")]
    public void Timeframe_ReadsAsTheRuleStatesIt(double hours, string expected) =>
        Cms0057Console.Timeframe(hours).Should().Be(expected);
}

/// <summary>The client reads authorization-service's string enums and calls the CMS-0057-F endpoints.</summary>
public class Cms0057ServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static (Cms0057Service, Handler) Create(string json)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AuthorizationService"] = "http://auth/api/",
        }).Build();
        return (new Cms0057Service(new HttpClient(handler), config, NullLogger<Cms0057Service>.Instance), handler);
    }

    [Fact]
    public async Task DecisionClocks_ReadEveryOpenRequest_IncludingStringEnums()
    {
        var (sut, handler) = Create("""[{"id":"a","authorizationNumber":"PA-1","status":"Pended","levelOfService":"U","hoursRemaining":3,"escalationLevel":"Critical"}]""");

        var clocks = await sut.GetDecisionClocksAsync();

        handler.Urls.Single().Should().Be("http://auth/api/authorizations/sla/at-risk?minLevel=None");
        clocks.Single().Expedited.Should().BeTrue();
        clocks.Single().EscalationLevel.Should().Be("Critical");
    }

    [Fact]
    public async Task Decisions_AndMetrics_CallTheCms0057Endpoints()
    {
        var (sut, handler) = Create("""[]""");
        await sut.GetRecentDecisionsAsync(30, 100);
        handler.Urls.Single().Should().Be("http://auth/api/authorizations/cms-0057/decisions?days=30&limit=100");

        var (metricsSut, metricsHandler) = Create("""{"totalRequests":4,"standard":{"approvedPercent":75.0}}""");
        var metrics = await metricsSut.GetMetricsAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        metricsHandler.Urls.Single().Should().StartWith("http://auth/api/authorizations/cms-0057/metrics?from=2026-01-01");
        metrics!.Standard.ApprovedPercent.Should().Be(75);
    }

    [Theory]
    [InlineData("\"Approved\"", 4, "Approved")]
    [InlineData("\"denied\"", 6, "Denied")]
    [InlineData("5", 5, "Modified")]
    public void AuthorizationSummary_ReadsTheStatusByNameOrNumber(string status, int number, string text)
    {
        var summary = JsonSerializer.Deserialize<AuthorizationSummary>(
            $$"""{"authorizationNumber":"PA-1","status":{{status}}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        summary.Status.Should().Be(number);
        summary.StatusText.Should().Be(text);
    }
}
