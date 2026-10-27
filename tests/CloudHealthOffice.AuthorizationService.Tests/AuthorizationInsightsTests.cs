using System.Net;
using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// Authorization Insights: the period figures are the CMS-0057 reporting
/// figures, and every figure's drill-down lists exactly the authorizations
/// it counts.
/// </summary>
public class AuthorizationInsightsTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime From = Now.AddDays(-30);

    private static Authorization Auth(string id, AuthorizationStatus status, double submittedHoursAgo, double? decidedAfterHours = null,
        bool expedited = false, string? denial = null, bool automated = false, LineOfBusiness lob = LineOfBusiness.Medicaid)
    {
        var submitted = Now.AddHours(-submittedHoursAgo);
        var a = new Authorization
        {
            Id = id, AuthorizationNumber = "PA-" + id, TenantId = "t1", MemberId = "M1", LineOfBusiness = lob,
            Status = status, SubmittedDate = submitted, LevelOfService = expedited ? "U" : "E",
            DenialReasonCode = denial, DenialReason = denial is null ? null : "Not medically necessary",
        };
        if (decidedAfterHours is { } h)
        {
            a.ReviewedDate = submitted.AddHours(h);
            a.StatusHistory.Add(new AuthorizationStatusChange { Status = status, ChangedAt = a.ReviewedDate.Value, Source = automated ? "automated-rule" : "reviewer" });
        }
        return a;
    }

    private static readonly List<Authorization> Activity =
    [
        Auth("s1", AuthorizationStatus.Approved, 200, decidedAfterHours: 24, automated: true),
        Auth("s2", AuthorizationStatus.Denied, 300, decidedAfterHours: 200, denial: "A1"),
        Auth("s3", AuthorizationStatus.Modified, 100, decidedAfterHours: 50),
        Auth("e1", AuthorizationStatus.Approved, 90, decidedAfterHours: 10, expedited: true, automated: true),
        Auth("e2", AuthorizationStatus.Denied, 150, decidedAfterHours: 80, expedited: true, denial: "A1"),
        Auth("o1", AuthorizationStatus.InReview, 130),
        Auth("old", AuthorizationStatus.Approved, 24 * 45, decidedAfterHours: 5),
        Auth("comm", AuthorizationStatus.Approved, 50, decidedAfterHours: 5, lob: LineOfBusiness.Commercial),
    ];

    private static readonly List<Authorization> Open =
    [
        Auth("q1", AuthorizationStatus.Submitted, 10),
        Auth("q2", AuthorizationStatus.InReview, 130),                     // standard, past 120h: Warning
        Auth("q3", AuthorizationStatus.Pended, 80, expedited: true),       // expedited, past 72h: Breach
        Auth("q4", AuthorizationStatus.Pended, 10, lob: LineOfBusiness.Commercial),
    ];

    private static AuthorizationInsights Insights() =>
        AuthorizationInsightSets.Build(Activity, Open, From, Now, LineOfBusiness.Medicaid, Now);

    private static int Count(AuthorizationInsightCriteria c) =>
        AuthorizationInsightSets.List(c, Activity, Open, From, Now, LineOfBusiness.Medicaid, 1000).Count;

    [Fact]
    public void Period_figures_are_the_cms_0057_figures()
    {
        var expected = Cms0057Reporting.ComputeMetrics(Activity, From, Now, LineOfBusiness.Medicaid);

        var reporting = Insights().Reporting;

        Assert.Equal(expected.TotalRequests, reporting.TotalRequests);
        Assert.Equal(6, reporting.TotalRequests);
        Assert.Equal(expected.Standard.DecidedWithinTimeframe, reporting.Standard.DecidedWithinTimeframe);
        Assert.Equal(2, reporting.Standard.DecidedWithinTimeframe);
        Assert.Equal(1, reporting.Expedited.DecidedWithinTimeframe);
        Assert.Equal(2, reporting.AutomatedDecisions);
    }

    [Fact]
    public void Every_period_figure_matches_its_drill_down()
    {
        var r = Insights().Reporting;

        Assert.Equal(r.TotalRequests, Count(new()));
        Assert.Equal(r.DecidedRequests, Count(new() { Outcome = "Decided" }));
        Assert.Equal(r.OpenRequests, Count(new() { Outcome = "Open" }));
        Assert.Equal(r.AutomatedDecisions, Count(new() { Automated = true }));
        foreach (var (type, m) in new[] { ("Standard", r.Standard), ("Expedited", r.Expedited) })
        {
            Assert.Equal(m.Requests, Count(new() { RequestType = type }));
            Assert.Equal(m.Approved, Count(new() { RequestType = type, Outcome = "Approved" }));
            Assert.Equal(m.Denied, Count(new() { RequestType = type, Outcome = "Denied" }));
            Assert.Equal(m.DecidedWithinTimeframe, Count(new() { RequestType = type, WithinTimeframe = true }));
            Assert.Equal(m.Decided - m.DecidedWithinTimeframe, Count(new() { RequestType = type, WithinTimeframe = false }));
        }
    }

    [Fact]
    public void The_queue_counts_open_requests_by_status_and_sla_in_the_line_of_business()
    {
        var q = Insights().Queue;

        Assert.Equal(3, q.Total);
        Assert.Equal(1, q.Expedited);
        Assert.Equal(1, q.BySla.Single(s => s.Key == "Warning").Count);
        Assert.Equal(1, q.BySla.Single(s => s.Key == "Breach").Count);
        foreach (var s in q.BySla)
            Assert.Equal(s.Count, Count(new() { Scope = "queue", Sla = Enum.Parse<SlaEscalationLevel>(s.Key) }));
        foreach (var s in q.ByStatus)
            Assert.Equal(s.Count, Count(new() { Scope = "queue", Status = Enum.Parse<AuthorizationStatus>(s.Key) }));
    }

    [Fact]
    public void Denial_reasons_match_their_drill_down()
    {
        var reasons = Insights().DenialReasons;
        var top = reasons[0];

        Assert.Equal("A1", top.Code);
        Assert.Equal(2, top.Count);
        Assert.Equal("Not medically necessary", top.Description);
        Assert.Equal(2, Count(new() { DenialReasonCode = "A1" }));

        // A reduction (s3, Modified) recorded without a reason is shown, not hidden.
        var unspecified = Assert.Single(reasons, r => r.Code == AuthorizationInsightSets.UnspecifiedDenialCode);
        Assert.Equal(1, unspecified.Count);
        Assert.Equal(1, Count(new() { DenialReasonCode = AuthorizationInsightSets.UnspecifiedDenialCode }));
        Assert.Equal(2, reasons.Count);
    }
}

/// <summary>The insights endpoints through the real pipeline.</summary>
public class AuthorizationInsightsEndpointTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly AuthorizationApiFactory _f;

    public AuthorizationInsightsEndpointTests(AuthorizationApiFactory factory)
    {
        _f = factory;
        _f.AuthorizationRepository.ListActivityBetweenAsync(default, default).ReturnsForAnyArgs(new List<Authorization>
        {
            new() { Id = "a1", TenantId = "tenant-ins", Status = AuthorizationStatus.Approved, SubmittedDate = DateTime.UtcNow.AddDays(-2), ReviewedDate = DateTime.UtcNow.AddDays(-1) },
        });
        _f.AuthorizationRepository.GetOpenAuthorizationsAsync(default).ReturnsForAnyArgs(new List<Authorization>());
    }

    private HttpClient As(string role)
    {
        var client = _f.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-ins");
        return client;
    }

    [Fact]
    public async Task UM_reads_insights_and_their_drill_down()
    {
        var insights = await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/insights");
        Assert.Equal(HttpStatusCode.OK, insights.StatusCode);
        using var doc = JsonDocument.Parse(await insights.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("reporting").GetProperty("totalRequests").GetInt32());

        var list = await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/insights/list?outcome=Approved");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var rows = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("a1", Assert.Single(rows.RootElement.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Insights_need_authorizations_read() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await As(ChoRolePermissions.Finance).GetAsync("/api/authorizations/insights")).StatusCode);

    [Theory]
    [InlineData("/api/authorizations/insights?from=2026-10-01&to=2026-09-01")]
    [InlineData("/api/authorizations/insights/list?scope=everything")]
    public async Task Bad_requests_are_refused(string url) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await As(ChoRolePermissions.UMCoordinator).GetAsync(url)).StatusCode);
}
