using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Reports;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// UM standard reports: decision timeliness against each request's own
/// deadline (72h expedited, 7 days standard, or its extension), by month,
/// and the open requests by deadline.
/// </summary>
public class UmReportBuilderTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    internal static Authorization Auth(string id, AuthorizationStatus status, DateTime submitted, double? decidedAfterHours = null,
        bool expedited = false, LineOfBusiness lob = LineOfBusiness.Medicaid, bool automated = false)
    {
        var a = new Authorization
        {
            Id = id, AuthorizationNumber = "PA-" + id, TenantId = "t1", MemberId = "MBR-" + id, PatientFirstName = "Secret",
            LineOfBusiness = lob, Status = status, SubmittedDate = submitted, LevelOfService = expedited ? "U" : "E",
            RequestingProviderNPI = "1234567890", ServiceTypeCode = "1",
        };
        if (decidedAfterHours is { } h)
        {
            a.ReviewedDate = submitted.AddHours(h);
            a.StatusHistory.Add(new AuthorizationStatusChange { Status = status, ChangedAt = a.ReviewedDate.Value, Source = automated ? "automated-rule" : "reviewer" });
        }
        return a;
    }

    [Fact]
    public void Decision_rows_judge_each_request_against_its_own_deadline()
    {
        var sept = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Auth("on-time", AuthorizationStatus.Approved, sept, 100, automated: true),
            Auth("late", AuthorizationStatus.Denied, sept, 200),
            Auth("exp-late", AuthorizationStatus.Approved, sept, 80, expedited: true),
            Auth("overdue", AuthorizationStatus.InReview, sept),
            Auth("not-due", AuthorizationStatus.Submitted, Now.AddHours(-10)),
        }.Select(a => UmReportBuilder.Decision(a, Now)).ToDictionary(r => r.AuthorizationId);

        Assert.Equal((true, "Approved", 100.0, true), (rows["on-time"].WithinTimeframe, rows["on-time"].Decision, rows["on-time"].HoursToDecision, rows["on-time"].Automated));
        Assert.False(rows["late"].WithinTimeframe);
        Assert.Equal(("Expedited", false), (rows["exp-late"].RequestType, rows["exp-late"].WithinTimeframe));
        Assert.Equal((false, (DateTime?)null, "InReview"), (rows["overdue"].WithinTimeframe, rows["overdue"].DecidedAt, rows["overdue"].Decision));
        Assert.Null(rows["not-due"].WithinTimeframe);
        Assert.Equal(sept.AddDays(7), rows["late"].DueAt);
        Assert.DoesNotContain(typeof(UmDecisionRow).GetProperties(), p => p.Name.Contains("Member") || p.Name.Contains("Patient"));
        Assert.DoesNotContain(typeof(UmPendingRow).GetProperties(), p => p.Name.Contains("Member") || p.Name.Contains("Patient"));
    }

    [Fact]
    public void Received_takes_the_period_line_of_business_and_request_type()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var all = new[]
        {
            Auth("in", AuthorizationStatus.Approved, from.AddDays(1), 5),
            Auth("exp", AuthorizationStatus.Approved, from.AddDays(2), 5, expedited: true),
            Auth("comm", AuthorizationStatus.Approved, from.AddDays(1), 5, lob: LineOfBusiness.Commercial),
            Auth("before", AuthorizationStatus.Approved, from.AddDays(-1), 5),
            Auth("at-to", AuthorizationStatus.Approved, Now, 0),
        };

        Assert.Equal(new[] { "in", "exp" }, UmReportBuilder.Received(all, from, Now, LineOfBusiness.Medicaid, null).Select(a => a.Id));
        Assert.Equal(new[] { "exp" }, UmReportBuilder.Received(all, from, Now, LineOfBusiness.Medicaid, "expedited").Select(a => a.Id));
    }

    [Fact]
    public void By_month_counts_on_time_late_and_open_per_request_type()
    {
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var aug = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
        var received = new[]
        {
            Auth("a1", AuthorizationStatus.Approved, aug, 24),
            Auth("a2", AuthorizationStatus.Approved, aug, 24),
            Auth("a3", AuthorizationStatus.Denied, aug, 300),
            Auth("a4", AuthorizationStatus.InReview, aug),
            Auth("x1", AuthorizationStatus.Approved, aug, 10, expedited: true),
            Auth("o1", AuthorizationStatus.Submitted, Now.AddHours(-1)),
        };

        var rows = UmReportBuilder.ByMonth(received, from, Now, Now);

        Assert.Equal(8, rows.Count); // Aug-Nov, standard and expedited each
        var augStd = rows.Single(r => r.ReceiptMonth == "2026-08" && r.RequestType == "Standard");
        Assert.Equal((4, 2, 1, 1, 0), (augStd.Received, augStd.DecidedOnTime, augStd.DecidedLate, augStd.OpenPastDue, augStd.OpenNotDue));
        Assert.Equal(0.6667m, augStd.PercentOnTime);
        var augExp = rows.Single(r => r.ReceiptMonth == "2026-08" && r.RequestType == "Expedited");
        Assert.Equal((1, 1, 1m), (augExp.Received, augExp.DecidedOnTime, augExp.PercentOnTime!.Value));
        var novStd = rows.Single(r => r.ReceiptMonth == "2026-11" && r.RequestType == "Standard");
        Assert.Equal((1, 1, (decimal?)null), (novStd.Received, novStd.OpenNotDue, novStd.PercentOnTime));
        Assert.Null(rows.Single(r => r.ReceiptMonth == "2026-09" && r.RequestType == "Standard").PercentOnTime);
    }

    [Fact]
    public void Open_requests_are_soonest_deadline_first_with_hours_left()
    {
        var open = new[]
        {
            Auth("std", AuthorizationStatus.InReview, Now.AddHours(-24)),          // due in 144h
            Auth("exp", AuthorizationStatus.Pended, Now.AddHours(-80), expedited: true), // 8h overdue
            Auth("done", AuthorizationStatus.Approved, Now.AddHours(-5), 1),
        };

        var rows = UmReportBuilder.Open(open, null, null).Select(a => UmReportBuilder.Pending(a, Now)).ToList();

        Assert.Equal(new[] { "exp", "std" }, rows.Select(r => r.AuthorizationId));
        Assert.Equal((-8.0, 144.0), (rows[0].HoursLeft, rows[1].HoursLeft));
        Assert.Equal(SlaEscalationLevel.Breach, rows[0].SlaLevel);
    }
}

/// <summary>
/// The UM reports API: reports:um or reports:compliance; a row-level run
/// (JSON or CSV) is in the record access log with every request it returned.
/// </summary>
public class UmReportsApiTests : IClassFixture<AuthorizationApiFactory>
{
    private const string Tenant = "tenant-reports";

    private readonly AuthorizationApiFactory _factory;
    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public UmReportsApiTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient With(string subject, params string[] permissions)
    {
        var client = _host.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.IssueTokenWithPermissions(Tenant, subject, permissions));
        return client;
    }

    [Fact]
    public async Task A_um_coordinator_without_reports_um_is_refused_and_logged()
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler("um-plain", ChoRolePermissions.UMCoordinator));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/authorizations/reports/" + UmReportCatalog.PendingByDeadline);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(_log.All, e => e.ActorId == "um-plain" && e.Action == "Report" && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_catalog_lists_the_um_reports_for_compliance()
    {
        var response = await With("comp-1", "reports:compliance").GetAsync("/api/authorizations/reports");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetArrayLength());
        Assert.Equal("reports:um,reports:compliance", body.RootElement[0].GetProperty("permission").GetString());
    }

    [Fact]
    public async Task A_decision_timeliness_run_logs_every_request_and_carries_no_member()
    {
        var received = DateTime.UtcNow.AddDays(-10);
        var a = UmReportBuilderTests.Auth("rpt-1", AuthorizationStatus.Approved, received, 30);
        var b = UmReportBuilderTests.Auth("rpt-2", AuthorizationStatus.Denied, received, 300);
        _factory.AuthorizationRepository.ListActivityBetweenAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(new[] { a, b });

        var response = await With("um-lead-1", "reports:um").GetAsync($"/api/authorizations/reports/{UmReportCatalog.DecisionTimeliness}?withinTimeframe=false");

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("MBR-", text);
        Assert.DoesNotContain("Secret", text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("rpt-2", Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("authorizationId").GetString());

        var e = Assert.Single(_log.All, x => x.ActorId == "um-lead-1");
        Assert.Equal(("Report", "Authorization"), (e.Action, e.RecordType));
        Assert.Equal(new[] { "rpt-2" }, e.RecordIds.Count > 0 ? e.RecordIds : [e.RecordId!]);
    }

    [Fact]
    public async Task A_csv_pending_run_is_a_file_and_still_logs_its_requests()
    {
        var a = UmReportBuilderTests.Auth("open-1", AuthorizationStatus.InReview, DateTime.UtcNow.AddHours(-30));
        var b = UmReportBuilderTests.Auth("open-2", AuthorizationStatus.Pended, DateTime.UtcNow.AddHours(-10), expedited: true);
        _factory.AuthorizationRepository.GetOpenAuthorizationsAsync(Tenant).Returns(new[] { a, b });

        var response = await With("um-lead-2", "reports:um").GetAsync($"/api/authorizations/reports/{UmReportCatalog.PendingByDeadline}?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).TrimEnd().Split(Environment.NewLine);
        Assert.StartsWith("AuthorizationId,AuthorizationNumber,RequestType", lines[0]);
        Assert.StartsWith("open-2,PA-open-2,Expedited", lines[1]);

        var e = Assert.Single(_log.All, x => x.ActorId == "um-lead-2");
        Assert.Equal(new[] { "open-2", "open-1" }, e.RecordIds);
    }

    [Fact]
    public async Task An_unknown_request_type_is_a_400()
    {
        var response = await With("um-lead-3", "reports:um").GetAsync($"/api/authorizations/reports/{UmReportCatalog.PendingByDeadline}?requestType=Urgent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
