using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Reports.Texas;
using AppealsService.Tests.Integration;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppealsService.Tests;

/// <summary>HHSC UMCM 5.24.6 member appeals and 5.24.5 complaints fixed-width files.</summary>
public class TexasAgTextFilesTests
{
    // 2026-09-15 10:00 Central (CDT, UTC-5).
    private static readonly DateTime Sept15 = new(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TexasAgFileRequest September = new(2026, 9, "01", "SUP", "4F");

    private static Appeal Case(string id, DateTime received, AppealType type = AppealType.Reconsideration,
        AppealStatus status = AppealStatus.InReview, AppealClosureReasonCode? code = null, DateTime? decided = null, bool urgent = false) => new()
    {
        Id = id, TenantId = "t1", AppealNumber = $"APL-20260915-{id.ToUpperInvariant().PadLeft(8, '0')}", MemberId = "M-" + id,
        ProviderNPI = "1234567890", AppealType = type, IsUrgent = urgent, LineOfBusiness = LineOfBusiness.Medicaid,
        Status = status, ClosureReasonCode = code, SubmittedDate = received, ReceivedDate = received,
        DecisionDate = decided, ClosedAt = decided,
    };

    private static TexasAgCodes AppealCodes(string id) => new()
    {
        TenantId = "t1", AppealId = id, Program = "01", StateMemberId = "123456789", MethodReceived = "W", ReceivedBy = "01",
        BenefitType = "AC", ReasonForDenial = "DSP", AdverseBenefitDetermination = "CD", PriorAuthorizationNumber = "PA-77",
    };

    private static TexasAgCodes ComplaintCodes(string id) => new()
    {
        TenantId = "t1", AppealId = id, Program = "01", StateMemberId = "987654321", MethodReceived = "V", ReceivedBy = "01",
        Complainant = "M", Mdcp = "N", BenefitType = "AC", ComplaintCategory = "AC", ComplaintCategoryDetail = "AC06",
        Disposition = "CD016", ComplaintOutcome = "CON",
    };

    private static Dictionary<string, TexasAgCodes> Codes(params TexasAgCodes[] codes) => codes.ToDictionary(c => c.AppealId);

    private static string Field(string line, IReadOnlyList<FixedWidthField> layout, string name)
    {
        var f = layout.Single(x => x.Name == name);
        return line.Substring(f.Start - 1, f.Width);
    }

    [Fact]
    public void The_layouts_match_the_hhsc_positions()
    {
        TexasAgTextFiles.AppealsLayout[^1].Should().Be(new FixedWidthField("PriorAuthorizationNumber", 193, 50));
        TexasAgTextFiles.AppealsLayout[^1].End.Should().Be(242);
        TexasAgTextFiles.ComplaintsLayout[^1].Should().Be(new FixedWidthField("ComplaintOutcome", 251, 3));
        TexasAgTextFiles.ComplaintsLayout.Single(f => f.Name == "DateResolved").Start.Should().Be(243);
    }

    [Theory]
    [InlineData(2026, 9, 2027)]
    [InlineData(2026, 8, 2026)]
    [InlineData(2027, 1, 2027)]
    public void The_state_fiscal_year_runs_september_to_august(int year, int month, int sfy) =>
        TexasAgTextFiles.StateFiscalYear(year, month).Should().Be(sfy);

    [Fact]
    public void A_decided_appeal_is_one_fixed_width_line_with_its_outcome_and_resolution_date()
    {
        var a = Case("a1", Sept15, status: AppealStatus.Closed, code: AppealClosureReasonCode.Approved, decided: Sept15.AddDays(10), urgent: true);
        var codes = AppealCodes("a1");
        codes.OverturnedOutcome = "P2P";

        var result = TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [a], Codes(codes));

        result.Problems.Should().BeEmpty();
        var line = result.Text.TrimEnd('\r', '\n');
        line.Length.Should().Be(242);
        string F(string n) => Field(line, TexasAgTextFiles.AppealsLayout, n);
        F("Sequence").Should().Be("000001");
        (F("McoName"), F("ReportingMonth"), F("StateFiscalYear"), F("Program"), F("PlanCode")).Should().Be(("SUP", "09", "2027", "01", "4F"));
        F("TrackingId").Should().Be("260915000000A1 ");
        F("MemberId").Should().Be("123456789");
        F("DateReceived").Should().Be("09152026");
        F("AppealTimeframe").Should().Be("72");
        (F("ExtensionNeeded"), F("ExtensionRequestedBy")).Should().Be(("N", "00"));
        F("ResolutionDate").Should().Be("09252026");
        (F("AppealOutcome"), F("OverturnedOutcome")).Should().Be(("OV", "P2P"));
        F("PriorAuthorizationNumber").TrimEnd().Should().Be("PA-77");
        result.Items.Single().Should().Be(new TexasAgFileRow(1, "260915000000A1", "a1", "M-a1", false));
    }

    [Fact]
    public void An_appeal_resolved_after_the_month_is_pending_with_no_date_and_one_closed_before_is_left_out()
    {
        var pending = Case("p1", Sept15, status: AppealStatus.Closed, code: AppealClosureReasonCode.Denied, decided: new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc));
        var open = Case("p2", Sept15.AddDays(-40));
        var closedBefore = Case("p3", Sept15.AddDays(-40), status: AppealStatus.Closed, code: AppealClosureReasonCode.Denied, decided: Sept15.AddDays(-20));
        var nextMonth = Case("p4", new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc)); // 01:00 Central on Oct 1

        var result = TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [pending, open, closedBefore, nextMonth],
            Codes(AppealCodes("p1"), AppealCodes("p2"), AppealCodes("p3"), AppealCodes("p4")));

        result.Items.Select(i => i.AppealId).Should().Equal("p2", "p1");
        result.Items.Should().OnlyContain(i => i.Pending);
        foreach (var line in result.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Field(line, TexasAgTextFiles.AppealsLayout, "ResolutionDate").Should().Be("        ");
            Field(line, TexasAgTextFiles.AppealsLayout, "AppealOutcome").Should().Be("PN");
        }
    }

    [Fact]
    public void A_case_missing_a_required_code_is_left_out_and_listed()
    {
        var a = Case("m1", Sept15);
        var codes = AppealCodes("m1");
        codes.StateMemberId = null;
        codes.ReasonForDenial = "OTH";

        var result = TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [a], Codes(codes));

        result.Text.Should().BeEmpty();
        result.Complete.Should().BeFalse();
        result.Problems.Select(p => p.Field).Should().BeEquivalentTo(["StateMemberId", "OtherReasonForDenial"]);
    }

    [Fact]
    public void A_medicaid_case_with_no_program_is_a_problem_and_another_programs_case_is_not()
    {
        var none = Case("n1", Sept15);
        var other = Case("n2", Sept15);
        var otherCodes = AppealCodes("n2");
        otherCodes.Program = "05";

        var result = TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [none, other], Codes(otherCodes));

        result.Items.Should().BeEmpty();
        result.Problems.Should().ContainSingle(p => p.AppealId == "n1" && p.Field == "Program");
    }

    [Fact]
    public void An_appeal_closed_without_a_decision_needs_its_hhsc_outcome()
    {
        var a = Case("x1", Sept15, status: AppealStatus.Closed, code: AppealClosureReasonCode.Expired, decided: Sept15.AddDays(3));
        TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [a], Codes(AppealCodes("x1")))
            .Problems.Should().ContainSingle(p => p.Field == "AppealOutcome");

        var codes = AppealCodes("x1");
        codes.AppealOutcome = "DM";
        var line = TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [a], Codes(codes)).Text;
        Field(line, TexasAgTextFiles.AppealsLayout, "AppealOutcome").Should().Be("DM");
    }

    [Fact]
    public void A_complaint_resolved_next_business_day_is_an_initial_contact_complaint()
    {
        // Friday 2026-09-18 received; resolved Monday 2026-09-21: within one business day.
        var friday = new DateTime(2026, 9, 18, 20, 0, 0, DateTimeKind.Utc);
        var g = Case("g1", friday, AppealType.Grievance, AppealStatus.Closed, AppealClosureReasonCode.Approved, friday.AddDays(3));

        var result = TexasAgTextFiles.Build(TexasAgFile.Complaints, September, [g], Codes(ComplaintCodes("g1")));

        result.Problems.Should().BeEmpty();
        var line = result.Text.TrimEnd('\r', '\n');
        line.Length.Should().Be(253);
        string F(string n) => Field(line, TexasAgTextFiles.ComplaintsLayout, n);
        (F("ComplaintType"), F("Complainant"), F("MemberId"), F("Mdcp")).Should().Be(("M", "M", "987654321", "N"));
        F("ProviderNpi").Should().Be("          ");
        F("InitialContactComplaint").Should().Be("Y");
        (F("Disposition"), F("ComplaintCategory"), F("ComplaintCategoryDetail")).Should().Be(("CD016", "AC", "AC06"));
        (F("DisasterRelated"), F("DateResolved"), F("ComplaintOutcome")).Should().Be(("N", "09212026", "CON"));
    }

    [Fact]
    public void A_provider_complaint_has_no_member_and_a_withdrawn_one_is_wtd()
    {
        var g = Case("g2", Sept15, AppealType.Grievance, AppealStatus.Closed, AppealClosureReasonCode.Withdrawn, Sept15.AddDays(5));
        g.ProviderNPI = "";
        var codes = ComplaintCodes("g2");
        codes.ComplaintType = "P";
        codes.Complainant = "P";
        codes.ComplaintOutcome = null;
        codes.Disposition = "CD008";

        var line = TexasAgTextFiles.Build(TexasAgFile.Complaints, September, [g], Codes(codes)).Text;

        string F(string n) => Field(line, TexasAgTextFiles.ComplaintsLayout, n);
        (F("MemberId"), F("Mdcp"), F("ProviderNpi")).Should().Be(("         ", " ", "9999999999"));
        (F("InitialContactComplaint"), F("ComplaintOutcome")).Should().Be(("N", "WTD"));
    }

    [Fact]
    public void Appeals_and_complaints_go_to_their_own_files()
    {
        var appeal = Case("k1", Sept15);
        var grievance = Case("k2", Sept15, AppealType.Grievance);
        var codes = Codes(AppealCodes("k1"), ComplaintCodes("k2"));

        TexasAgTextFiles.Build(TexasAgFile.MemberAppeals, September, [appeal, grievance], codes).Items.Select(i => i.AppealId).Should().Equal("k1");
        var complaints = TexasAgTextFiles.Build(TexasAgFile.Complaints, September, [appeal, grievance], codes);
        complaints.Items.Select(i => i.AppealId).Should().Equal("k2");
        Field(complaints.Text, TexasAgTextFiles.ComplaintsLayout, "ComplaintOutcome").Should().Be("PND");
    }

    [Fact]
    public void A_complaint_detail_must_belong_to_its_category()
    {
        var codes = ComplaintCodes("c1");
        codes.ComplaintCategoryDetail = "QC01";
        TexasAgTextFiles.Build(TexasAgFile.Complaints, September, [Case("c1", Sept15, AppealType.Grievance)], Codes(codes))
            .Problems.Should().ContainSingle(p => p.Field == "ComplaintCategoryDetail");
    }

    [Fact]
    public void The_tracking_id_comes_from_the_appeal_number_and_fits_15()
    {
        var a = Case("t1", Sept15);
        a.AppealNumber = "APL-20260915-ABCD1234";
        TexasAgTextFiles.TrackingId(a, new TexasAgCodes()).Should().Be("260915ABCD1234");
        a.AppealNumber = "PLAN-APPEAL-NUMBER-TOO-LONG";
        TexasAgTextFiles.TrackingId(a, new TexasAgCodes()).Should().BeNull();
        TexasAgTextFiles.TrackingId(a, new TexasAgCodes { TrackingId = "TX0001" }).Should().Be("TX0001");
    }

    [Fact]
    public void No_cases_is_a_blank_file()
    {
        var result = TexasAgTextFiles.Build(TexasAgFile.Complaints, September, [], new Dictionary<string, TexasAgCodes>());
        result.Text.Should().BeEmpty();
        result.Complete.Should().BeTrue();
    }

    [Fact]
    public void A_value_never_gets_cut_to_fit()
    {
        var record = new FixedWidthRecord(TexasAgTextFiles.AppealsLayout);
        var set = () => record.Set("PlanCode", "4FX");
        set.Should().Throw<ArgumentException>();
    }
}

/// <summary>The Texas codes and file endpoints: permissions, validation and the access log.</summary>
public class TexasAgReportsApiTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string Tenant = "tenant-tx-ag";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public TexasAgReportsApiTests(AppealsWebApplicationFactory factory)
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
        var um = As("um-tx", ChoRolePermissions.UMCoordinator);
        var response = await um.PostAsJsonAsync("/api/appeals", new CreateAppealRequest
        {
            ClaimId = "CLM-" + member, ClaimNumber = "CLM-" + member, MemberId = member, PatientName = "Pat Texas", ProviderNPI = "1234567890",
            AppealReason = "Needed.", LineOfBusiness = LineOfBusiness.Medicaid,
            AppealType = AppealType.Reconsideration, AppealLevel = AppealLevel.FirstLevel, AppealedAmount = 100m, DeniedAmount = 100m,
        }, Json);
        response.EnsureSuccessStatusCode();
        var appeal = (await response.Content.ReadFromJsonAsync<Appeal>(Json))!;
        (await um.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), Json)).EnsureSuccessStatusCode();
        return appeal;
    }

    private static object Codes => new
    {
        program = "01", stateMemberId = "123456789", methodReceived = "W", receivedBy = "01",
        benefitType = "AC", reasonForDenial = "DSP", adverseBenefitDetermination = "CD",
    };

    [Fact]
    public async Task Codes_are_validated_saved_and_versioned()
    {
        var a = await CreateAsync("M-TX-1");
        var um = As("um-tx", ChoRolePermissions.UMCoordinator);

        var bad = await um.PutAsJsonAsync($"/api/appeals/{a.Id}/texas-codes", new { program = "09", stateMemberId = "12" }, Json);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problems = await bad.Content.ReadAsStringAsync();
        problems.Should().Contain("Program").And.Contain("StateMemberId");

        (await um.PutAsJsonAsync($"/api/appeals/{a.Id}/texas-codes", Codes, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await um.PutAsJsonAsync($"/api/appeals/{a.Id}/texas-codes", new { program = "05" }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = (await um.GetFromJsonAsync<TexasAgCodes>($"/api/appeals/{a.Id}/texas-codes", Json))!;
        saved.Program.Should().Be("05");
        saved.UpdatedBy.Should().Be("um-tx");
        saved.History.Should().ContainSingle().Which.CodesJson.Should().Contain("123456789");

        (await um.PutAsJsonAsync("/api/appeals/no-such-appeal/texas-codes", Codes, Json)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_file_needs_report_permission_and_logs_its_cases()
    {
        var a = await CreateAsync("M-TX-2");
        var um = As("um-tx", ChoRolePermissions.UMCoordinator);
        (await um.PutAsJsonAsync($"/api/appeals/{a.Id}/texas-codes", Codes, Json)).EnsureSuccessStatusCode();
        // The current Texas month: the file refuses a month that has not begun in Central time.
        var month = TexasAgTextFiles.TexasDate(DateTime.UtcNow).ToString("yyyy-MM");
        var query = $"?month={month}&program=01&mcoCode=sup&planCode=4F";

        (await um.GetAsync("/api/appeals/reports/texas/member-appeals" + query)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var file = await With("comp-tx", "reports:compliance").GetAsync("/api/appeals/reports/texas/member-appeals" + query + "&format=txt");
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        var text = await file.Content.ReadAsStringAsync();
        text.Should().Contain("123456789").And.NotContain("Pat Texas");
        _log.All.Single(e => e.ActorId == "comp-tx").RecordIds.Should().Contain(a.Id);

        var json = await With("comp-tx-2", "reports:compliance").GetFromJsonAsync<TexasAgFileView>("/api/appeals/reports/texas/member-appeals" + query, Json);
        json!.Items.Should().Contain(i => i.AppealId == a.Id && i.Pending);
    }

    [Fact]
    public async Task A_bad_request_is_a_400_and_an_unknown_file_a_404()
    {
        var c = With("comp-tx-3", "reports:compliance");
        (await c.GetAsync("/api/appeals/reports/texas/member-appeals?month=2026-09&program=99&mcoCode=SUP&planCode=4F")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.GetAsync("/api/appeals/reports/texas/member-appeals?month=Sept&program=01&mcoCode=SUP&planCode=4F")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.GetAsync("/api/appeals/reports/texas/hotlines?month=2026-09&program=01&mcoCode=SUP&planCode=4F")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
