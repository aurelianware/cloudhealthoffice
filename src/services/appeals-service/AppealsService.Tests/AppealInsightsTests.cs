using System.Net;
using System.Text.Json;
using AppealsService.Models;
using AppealsService.Tests.Integration;
using CloudHealthOffice.Infrastructure.Security;

namespace AppealsService.Tests;

/// <summary>
/// Appeals &amp; Grievances Insights: timeliness against the regulatory
/// deadline, overturn rate, the open queue and external review; every figure's
/// drill-down lists exactly the cases it counts.
/// </summary>
public class AppealInsightsTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddDays(-30);

    private static Appeal Closed(string id, AppealLevel level, AppealClosureReasonCode code, int receivedDaysAgo, int decidedAfterDays, int deadlineDays,
        bool urgent = false, bool extended = false, AppealType type = AppealType.Reconsideration, string? denial = "CO-50")
    {
        var received = Now.AddDays(-receivedDaysAgo);
        return new Appeal
        {
            Id = id, TenantId = "t1", AppealLevel = level, AppealType = type, IsUrgent = urgent, LineOfBusiness = LineOfBusiness.Medicaid,
            Status = AppealStatus.Closed, ClosureReasonCode = code, SubmittedDate = received, ReceivedDate = received,
            DecisionDate = received.AddDays(decidedAfterDays), ClosedAt = received.AddDays(decidedAfterDays),
            TargetResponseDate = received.AddDays(deadlineDays), DenialReasonCode = denial,
            DeadlineExtension = extended ? new AppealDeadlineExtension { ExtensionDays = 14 } : null,
        };
    }

    private static Appeal Open(string id, AppealStatus status, int dueInDays, bool urgent = false, AppealLevel level = AppealLevel.FirstLevel) => new()
    {
        Id = id, TenantId = "t1", Status = status, AppealLevel = level, IsUrgent = urgent, LineOfBusiness = LineOfBusiness.Medicaid,
        SubmittedDate = Now.AddDays(-10), ReceivedDate = Now.AddDays(-10), TargetResponseDate = Now.AddDays(dueInDays),
    };

    private static readonly List<Appeal> Activity =
    [
        Closed("a1", AppealLevel.FirstLevel, AppealClosureReasonCode.Approved, 20, 10, 30),                 // on time, overturned
        Closed("a2", AppealLevel.FirstLevel, AppealClosureReasonCode.Denied, 40, 35, 30),                   // received before, decided in the period: late, upheld
        Closed("a3", AppealLevel.FirstLevel, AppealClosureReasonCode.PartialApproval, 10, 2, 3, urgent: true), // expedited, on time
        Closed("a4", AppealLevel.SecondLevel, AppealClosureReasonCode.Denied, 45, 40, 44, extended: true),  // on time against the extended deadline
        Closed("a5", AppealLevel.ExternalReview, AppealClosureReasonCode.Approved, 15, 10, 45, denial: "CO-197"), // overturned at external review
        Closed("w1", AppealLevel.FirstLevel, AppealClosureReasonCode.Withdrawn, 12, 3, 30),                 // closed without a decision
        Closed("g1", AppealLevel.FirstLevel, AppealClosureReasonCode.Approved, 20, 5, 30, type: AppealType.Grievance, denial: null),
        Closed("old", AppealLevel.FirstLevel, AppealClosureReasonCode.Denied, 90, 10, 30),                  // decided before the period
    ];

    private static readonly List<Appeal> Queue =
    [
        Open("o1", AppealStatus.InReview, dueInDays: -2),
        Open("o2", AppealStatus.PendingInfo, dueInDays: 5, urgent: true),
        Open("o3", AppealStatus.Submitted, dueInDays: 20, level: AppealLevel.SecondLevel),
    ];

    private static AppealInsights Insights() => AppealInsightSets.Build(Activity, Queue, From, Now, LineOfBusiness.Medicaid, Now);

    private static int Count(AppealInsightCriteria c) =>
        AppealInsightSets.List(c, Activity, Queue, From, Now, LineOfBusiness.Medicaid, Now, 1000).Count;

    [Fact]
    public void Timeliness_is_measured_against_the_regulatory_deadline_including_extensions()
    {
        var first = Insights().ResolvedByLevel.Single(l => l.Level == "FirstLevel");
        first.Standard.Resolved.Should().Be(2);
        first.Standard.OnTime.Should().Be(1);
        first.Standard.Late.Should().Be(1);
        first.Expedited.OnTime.Should().Be(1);
        first.ClosedWithoutDecision.Should().Be(1);

        var second = Insights().ResolvedByLevel.Single(l => l.Level == "SecondLevel");
        second.Standard.OnTime.Should().Be(1);
        second.Standard.Extended.Should().Be(1);
    }

    [Fact]
    public void Overturn_rate_is_of_decided_appeals_and_not_reported_for_grievances()
    {
        var levels = Insights().ResolvedByLevel;

        var first = levels.Single(l => l.Level == "FirstLevel");
        first.Overturned.Should().Be(2);
        first.Upheld.Should().Be(1);
        first.OverturnPercent.Should().Be(66.7);
        levels.Single(l => l.Level == "ExternalReview").Overturned.Should().Be(1);
        var grievance = levels.Single(l => l.Level == "Grievance");
        grievance.OverturnPercent.Should().BeNull();
        grievance.Standard.Resolved.Should().Be(1);
    }

    [Fact]
    public void Every_figure_matches_its_drill_down()
    {
        var i = Insights();

        Count(new() { Scope = "received" }).Should().Be(i.Received.Total);
        Count(new() { Scope = "received", Kind = "Grievance" }).Should().Be(i.Received.Grievances);
        Count(new() { Scope = "received", Expedited = true }).Should().Be(i.Received.Expedited);
        Count(new() { Scope = "received", ExternalReview = true }).Should().Be(i.Received.ExternalReview);
        foreach (var level in i.ResolvedByLevel)
        {
            foreach (var (urgent, t) in new[] { (false, level.Standard), (true, level.Expedited) })
            {
                Count(new() { Scope = "resolved", Level = level.Level, Expedited = urgent, OnTime = true }).Should().Be(t.OnTime, $"{level.Level} on time");
                Count(new() { Scope = "resolved", Level = level.Level, Expedited = urgent, OnTime = false }).Should().Be(t.Late, $"{level.Level} late");
                Count(new() { Scope = "resolved", Level = level.Level, Expedited = urgent, Extended = true }).Should().Be(t.Extended);
            }
            Count(new() { Scope = "resolved", Level = level.Level, Outcome = "Overturned" }).Should().Be(level.Overturned);
            Count(new() { Scope = "resolved", Level = level.Level, Outcome = "Upheld" }).Should().Be(level.Upheld);
            Count(new() { Scope = "resolved", Level = level.Level, Outcome = "ClosedWithoutDecision" }).Should().Be(level.ClosedWithoutDecision);
        }
        Count(new() { Scope = "open" }).Should().Be(i.Open.Total);
        Count(new() { Scope = "open", Overdue = true }).Should().Be(i.Open.Overdue);
        Count(new() { Scope = "open", DueWithin7Days = true }).Should().Be(i.Open.DueWithin7Days);
        Count(new() { Scope = "open", PendingInfo = true }).Should().Be(i.Open.PendingInfo);
        foreach (var reason in i.AppealedDenialReasons)
            Count(new() { Scope = "received", DenialReasonCode = reason.Code }).Should().Be(reason.Count);
    }

    [Fact]
    public void The_open_queue_counts_overdue_due_soon_and_pending_info()
    {
        var open = Insights().Open;

        open.Total.Should().Be(3);
        open.Overdue.Should().Be(1);
        open.DueWithin7Days.Should().Be(1);
        open.PendingInfo.Should().Be(1);
        open.Expedited.Should().Be(1);
    }
}

/// <summary>The insights endpoints through the real pipeline.</summary>
public class AppealInsightsEndpointTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _f;

    public AppealInsightsEndpointTests(AppealsWebApplicationFactory factory) => _f = factory;

    private HttpClient As(string role)
    {
        var client = _f.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-ins");
        return client;
    }

    [Fact]
    public async Task UM_reads_insights_and_a_role_without_appeals_read_is_refused()
    {
        var ok = await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/appeals/insights");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("resolvedByLevel").GetArrayLength().Should().Be(4);

        (await As(ChoRolePermissions.Finance).GetAsync("/api/appeals/insights")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/appeals/insights?from=2026-10-01&to=2026-09-01")]
    [InlineData("/api/appeals/insights/list?scope=everything")]
    public async Task Bad_requests_are_refused(string url) =>
        (await As(ChoRolePermissions.UMCoordinator).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task The_drill_down_lists_matching_cases()
    {
        var list = await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/appeals/insights/list?scope=open&overdue=true");

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
