using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Reports;
using AppealsService.Tests.Integration;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppealsService.Tests;

/// <summary>
/// Appeals and grievances standard reports: the 438.416 case log,
/// timeliness by month and level against each case's own deadline, and the
/// open cases by deadline.
/// </summary>
public class AgReportBuilderTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Appeal Case(string id, DateTime received, int deadlineDays, AppealStatus status = AppealStatus.Closed,
        AppealClosureReasonCode? code = AppealClosureReasonCode.Denied, int? decidedAfterDays = 10,
        AppealType type = AppealType.Reconsideration, AppealLevel level = AppealLevel.FirstLevel, bool urgent = false) => new()
    {
        Id = id, AppealNumber = "APL-" + id, TenantId = "t1", MemberId = "M-" + id, PatientName = "Pat Secret", ProviderNPI = "1234567890",
        ClaimNumber = "CLM-" + id, DenialReasonCode = "CO-50", AppealReason = "secret reason",
        AppealType = type, AppealLevel = level, IsUrgent = urgent, LineOfBusiness = LineOfBusiness.Medicaid,
        Status = status, ClosureReasonCode = status == AppealStatus.Closed ? code : null,
        SubmittedDate = received, ReceivedDate = received, TargetResponseDate = received.AddDays(deadlineDays),
        DecisionDate = status == AppealStatus.Closed && decidedAfterDays is { } d ? received.AddDays(d) : null,
        ClosedAt = status == AppealStatus.Closed && decidedAfterDays is { } c ? received.AddDays(c) : null,
    };

    [Fact]
    public void The_case_log_names_the_member_and_resolution_but_not_the_reason()
    {
        var sept = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Case("on-time", sept, 30),
            Case("late", sept, 30, decidedAfterDays: 40),
            Case("withdrawn", sept, 30, code: AppealClosureReasonCode.Withdrawn),
            Case("overdue", sept, 30, status: AppealStatus.InReview, decidedAfterDays: null),
            Case("grv", sept, 90, code: AppealClosureReasonCode.Approved, type: AppealType.Grievance),
        }.Select(a => AgReportBuilder.Case(a, Now)).ToDictionary(r => r.AppealId);

        Assert.Equal(("M-on-time", true, "Denied", "Appeal", "FirstLevel"),
            (rows["on-time"].MemberId, rows["on-time"].OnTime, rows["on-time"].Resolution, rows["on-time"].Kind, rows["on-time"].Level));
        Assert.False(rows["late"].OnTime);
        Assert.Null(rows["withdrawn"].OnTime);
        Assert.Equal((false, (DateTime?)null, (string?)null), (rows["overdue"].OnTime, rows["overdue"].ResolvedAt, rows["overdue"].Resolution));
        Assert.Equal(("Grievance", "Grievance", (string?)null, (string?)null), (rows["grv"].Kind, rows["grv"].Level, rows["grv"].ClaimNumber, rows["grv"].DenialReasonCode));
        Assert.DoesNotContain(typeof(AgCaseRow).GetProperties(), p => p.Name.Contains("Reason") && p.Name != "DenialReasonCode");
        Assert.DoesNotContain(typeof(AgCaseRow).GetProperties(), p => p.Name.Contains("Patient"));
    }

    [Fact]
    public void By_month_counts_each_level_and_overturns_appeals_only()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var sept = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var received = new[]
        {
            Case("a1", sept, 30, code: AppealClosureReasonCode.Approved),
            Case("a2", sept, 30, decidedAfterDays: 45),
            Case("a3", sept, 30, code: AppealClosureReasonCode.Withdrawn),
            Case("a4", sept, 30, status: AppealStatus.PendingInfo, decidedAfterDays: null),
            Case("g1", sept, 90, code: AppealClosureReasonCode.Approved, type: AppealType.Grievance),
            Case("x1", sept, 45, code: AppealClosureReasonCode.Approved, level: AppealLevel.ExternalReview),
        };

        var rows = AgReportBuilder.ByMonth(received, from, Now, Now);

        Assert.Equal(12, rows.Count); // Sep-Nov x 4 levels
        var first = rows.Single(r => r.ReceiptMonth == "2026-09" && r.Level == "FirstLevel");
        Assert.Equal((4, 1, 1, 1, 1, 0), (first.Received, first.ResolvedOnTime, first.ResolvedLate, first.ClosedWithoutDecision, first.OpenPastDue, first.OpenNotDue));
        Assert.Equal((0.5m, 1, 0.5m), (first.PercentOnTime!.Value, first.Overturned, first.PercentOverturned!.Value));
        var grievance = rows.Single(r => r.ReceiptMonth == "2026-09" && r.Level == "Grievance");
        Assert.Equal((1, 0, (decimal?)null), (grievance.ResolvedOnTime, grievance.Overturned, grievance.PercentOverturned));
        Assert.Equal(1, rows.Single(r => r.ReceiptMonth == "2026-09" && r.Level == "ExternalReview").Overturned);
        Assert.Null(rows.Single(r => r.ReceiptMonth == "2026-10" && r.Level == "FirstLevel").PercentOnTime);
    }

    [Fact]
    public void Grievances_by_category_count_uncategorized_and_skip_appeals()
    {
        var sept = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var g1 = Case("g1", sept, 90, code: AppealClosureReasonCode.Approved, type: AppealType.Grievance);
        g1.GrievanceCategory = "Quality of Care";
        var g2 = Case("g2", sept, 30, decidedAfterDays: 40, type: AppealType.Grievance);
        g2.GrievanceCategory = "Quality of Care";
        var g3 = Case("g3", sept, 90, status: AppealStatus.InReview, decidedAfterDays: null, type: AppealType.Grievance);
        var appeal = Case("a1", sept, 30);

        var rows = AgReportBuilder.ByCategory([g1, g2, g3, appeal]);

        Assert.Equal(new[] { "Quality of Care", GrievanceCategories.Uncategorized }, rows.Select(r => r.Category));
        Assert.Equal((2, 1, 1, 0, 0.5m), (rows[0].Received, rows[0].ResolvedOnTime, rows[0].ResolvedLate, rows[0].Open, rows[0].PercentOnTime!.Value));
        Assert.Equal((1, 1), (rows[1].Received, rows[1].Open));
        Assert.Equal("Quality of Care", AgReportBuilder.Case(g1, Now).GrievanceCategory);
        Assert.Null(AgReportBuilder.Case(appeal, Now).GrievanceCategory);
    }

    [Fact]
    public void The_dhcs_list_has_the_25_apl_24_001_categories()
    {
        Assert.Equal(25, GrievanceCategories.DhcsMediCal.Count);
        Assert.Equal(GrievanceCategories.DhcsMediCal.Count, GrievanceCategories.DhcsMediCal.Distinct().Count());
    }

    [Fact]
    public void Open_cases_are_soonest_deadline_first_and_no_deadline_last()
    {
        var open = new[]
        {
            Case("later", Now.AddDays(-5), 25, status: AppealStatus.InReview, decidedAfterDays: null),
            Case("overdue", Now.AddDays(-40), 30, status: AppealStatus.Submitted, decidedAfterDays: null),
            Case("closed", Now.AddDays(-3), 30),
        }.ToList();
        var noDeadline = Case("none", Now.AddDays(-1), 1, status: AppealStatus.PendingInfo, decidedAfterDays: null);
        noDeadline.TargetResponseDate = null;
        open.Add(noDeadline);

        var rows = AgReportBuilder.Open(open, null, null).Select(a => AgReportBuilder.OpenCase(a, Now)).ToList();

        Assert.Equal(new[] { "overdue", "later", "none" }, rows.Select(r => r.AppealId));
        Assert.Equal((-10.0, 20.0), (rows[0].DaysLeft!.Value, rows[1].DaysLeft!.Value));
        Assert.Null(rows[2].DaysLeft);
    }
}

/// <summary>
/// The A&amp;G reports API: reports:um or reports:compliance; the case log
/// (JSON or CSV) is in the record access log with every case and member it
/// returned.
/// </summary>
public class AgReportsApiTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string Tenant = "tenant-ag-reports";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public AgReportsApiTests(AppealsWebApplicationFactory factory)
    {
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IRecordAccessStore>();
            s.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient As(string subject, string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient With(string subject, params string[] permissions)
    {
        var client = _host.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(subject, Tenant, Array.Empty<string>(), permissions: permissions));
        return client;
    }

    private async Task<Appeal> CreateAsync(string member)
    {
        var response = await As("um-create", ChoRolePermissions.UMCoordinator).PostAsJsonAsync("/api/appeals", new CreateAppealRequest
        {
            ClaimId = "CLM-" + member, ClaimNumber = "CLM-" + member, MemberId = member, PatientName = "Pat Report", ProviderNPI = "1234567890",
            AppealReason = "Medically necessary.", LineOfBusiness = LineOfBusiness.Medicaid,
            AppealType = AppealType.Reconsideration, AppealLevel = AppealLevel.FirstLevel, AppealedAmount = 100m, DeniedAmount = 100m,
        }, Json);
        response.EnsureSuccessStatusCode();
        var appeal = (await response.Content.ReadFromJsonAsync<Appeal>(Json))!;
        var submit = await As("um-create", ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), Json);
        submit.EnsureSuccessStatusCode();
        return appeal;
    }

    [Fact]
    public async Task A_um_coordinator_without_report_permission_is_refused_and_logged()
    {
        var response = await As("um-plain", ChoRolePermissions.UMCoordinator).GetAsync("/api/appeals/reports/" + AgReportCatalog.CaseLog);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _log.All.Should().Contain(e => e.ActorId == "um-plain" && e.Action == "Report" && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_case_log_logs_every_case_and_member_and_carries_no_name_or_reason()
    {
        var a = await CreateAsync("M-REP-1");
        var b = await CreateAsync("M-REP-2");

        var response = await With("comp-1", "reports:compliance").GetAsync("/api/appeals/reports/" + AgReportCatalog.CaseLog);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain("Pat Report").And.NotContain("Medically necessary");
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("appealId").GetString())
            .Should().Contain([a.Id, b.Id]);

        var e = _log.All.Single(x => x.ActorId == "comp-1");
        e.Action.Should().Be("Report");
        e.RecordIds.Should().Contain([a.Id, b.Id]);
        e.MemberIds.Should().Contain(["M-REP-1", "M-REP-2"]);
    }

    [Fact]
    public async Task A_csv_open_cases_run_is_a_file_and_still_logs_its_cases()
    {
        var a = await CreateAsync("M-REP-3");

        var response = await With("um-lead-1", "reports:um").GetAsync($"/api/appeals/reports/{AgReportCatalog.OpenByDeadline}?format=csv&kind=Appeal");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        (await response.Content.ReadAsStringAsync()).Should().StartWith("AppealId,AppealNumber,Kind,Level").And.Contain(a.Id);
        _log.All.Single(x => x.ActorId == "um-lead-1").RecordIds.Should().Contain(a.Id);
    }

    [Fact]
    public async Task A_grievance_keeps_its_category_and_an_appeal_cannot_have_one()
    {
        var um = As("um-create", ChoRolePermissions.UMCoordinator);
        CreateAppealRequest Request(AppealType type) => new()
        {
            ClaimId = "CLM-G", ClaimNumber = "CLM-G", MemberId = "M-G", PatientName = "Pat G", ProviderNPI = "1234567890",
            AppealReason = "Long wait.", LineOfBusiness = LineOfBusiness.Medicaid, AppealType = type, AppealLevel = AppealLevel.FirstLevel,
            GrievanceCategory = "Provider Availability / Scheduling",
        };

        var grievance = await um.PostAsJsonAsync("/api/appeals", Request(AppealType.Grievance), Json);
        grievance.StatusCode.Should().Be(HttpStatusCode.Created, await grievance.Content.ReadAsStringAsync());
        (await grievance.Content.ReadFromJsonAsync<Appeal>(Json))!.GrievanceCategory.Should().Be("Provider Availability / Scheduling");

        var appeal = await um.PostAsJsonAsync("/api/appeals", Request(AppealType.Reconsideration), Json);
        appeal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_kind_is_a_400()
    {
        var response = await With("um-lead-2", "reports:um").GetAsync($"/api/appeals/reports/{AgReportCatalog.OpenByDeadline}?kind=Complaint");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
