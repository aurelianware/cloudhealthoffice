using System.Net;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Reports;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Reports;

/// <summary>The claims standard reports' row building and 447.45 timeliness arithmetic.</summary>
public class ClaimReportBuilderTests
{
    private static readonly DateTime AsOf = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "0-15")]
    [InlineData(15, "0-15")]
    [InlineData(16, "16-30")]
    [InlineData(45, "31-45")]
    [InlineData(60, "46-60")]
    [InlineData(61, "61+")]
    [InlineData(400, "61+")]
    public void Age_buckets_match_the_insights_buckets(int days, string bucket) =>
        Assert.Equal(bucket, ClaimReportBuilder.AgeBucket(days));

    [Fact]
    public void A_pended_claim_shows_its_pend_code_and_age_and_no_member()
    {
        var claim = new Claim
        {
            Id = "c-1", ClaimNumber = "CN-1", MemberId = "MBR-SECRET", Status = ClaimStatus.Pended,
            SubmittedDate = AsOf.AddDays(-20.5), BillingProviderNPI = "1234567890", TotalChargeAmount = 250m,
            PendDetails = new PendDetails { PendCode = "MED-REVIEW", PendedAt = AsOf.AddDays(-19) },
            LineOfBusiness = LineOfBusiness.Medicaid,
        };

        var row = ClaimReportBuilder.Inventory(claim, AsOf);

        Assert.Equal(("MED-REVIEW", 20, "16-30"), (row.PendCode, row.AgeDays, row.AgeBucket));
        Assert.DoesNotContain(typeof(ClaimInventoryRow).GetProperties(), p => p.Name.Contains("Member"));
        Assert.DoesNotContain(typeof(ClaimDenialRow).GetProperties(), p => p.Name.Contains("Member"));
    }

    [Fact]
    public void A_pended_claim_without_pend_details_is_UNSPECIFIED_and_an_open_unpended_claim_has_no_pend_code()
    {
        var pended = ClaimReportBuilder.Inventory(new Claim { Id = "a", Status = ClaimStatus.Pended, SubmittedDate = AsOf }, AsOf);
        var received = ClaimReportBuilder.Inventory(new Claim { Id = "b", Status = ClaimStatus.Received, SubmittedDate = AsOf }, AsOf);

        Assert.Equal(ClaimCriteria.UnspecifiedPendCode, pended.PendCode);
        Assert.Null(received.PendCode);
        Assert.Null(received.PendedAt);
    }

    [Fact]
    public void Timeliness_counts_each_receipt_month_against_30_and_90_days()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var june = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var claims = new List<(DateTime, DateTime?)>();
        // June: 10 claims; 9 within 30 days, 1 at 45 days.
        for (var i = 0; i < 9; i++) claims.Add((june, june.AddDays(29)));
        claims.Add((june, june.AddDays(45)));
        // July: 2 claims; one at 100 days, one never adjudicated.
        var july = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);
        claims.Add((july, july.AddDays(100)));
        claims.Add((july, null));
        // September (window still open for 90 days): 1 claim adjudicated in 2 days.
        var sept = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        claims.Add((sept, sept.AddDays(2)));
        // Outside the period: ignored.
        claims.Add((to.AddDays(1), to.AddDays(2)));

        var rows = ClaimReportBuilder.Timeliness(claims, from, to, AsOf);

        Assert.Equal(new[] { "2026-06", "2026-07", "2026-08", "2026-09" }, rows.Select(r => r.ReceiptMonth));

        var jun = rows[0];
        Assert.Equal((10, 9, 10, 0, 0), (jun.Received, jun.Within30Days, jun.Within90Days, jun.After90Days, jun.StillOpen));
        Assert.Equal((0.9m, 1m), (jun.PercentWithin30!.Value, jun.PercentWithin90!.Value));
        Assert.Equal((true, true), (jun.Meets30DayStandard, jun.Meets90DayStandard));

        var jul = rows[1];
        Assert.Equal((2, 0, 0, 1, 1), (jul.Received, jul.Within30Days, jul.Within90Days, jul.After90Days, jul.StillOpen));
        Assert.Equal((false, false), (jul.Meets30DayStandard, jul.Meets90DayStandard));

        var aug = rows[2];
        Assert.Equal(0, aug.Received);
        Assert.Null(aug.PercentWithin30);
        Assert.Null(aug.Meets30DayStandard);

        var sep = rows[3];
        Assert.Equal(1, sep.Within30Days);
        Assert.True(sep.Meets30DayStandard);   // 30 Sep + 30 days is before 1 Nov
        Assert.Null(sep.Meets90DayStandard);   // 30 Sep + 90 days is not
    }

    [Fact]
    public void An_adjudication_after_the_as_of_instant_counts_as_still_open()
    {
        var r = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = ClaimReportBuilder.Timeliness([(r, AsOf.AddDays(1))], r, r.AddMonths(1), AsOf);
        Assert.Equal(1, rows[0].StillOpen);
    }

    [Fact]
    public void Csv_quotes_delimiters_and_neutralizes_formulas()
    {
        var row = new ClaimDenialRow("c-1", "=HYPERLINK(\"x\")", ClaimStatus.Denied, "CO-50", "Not medically necessary, per policy",
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, "1234567890", LineOfBusiness.Medicaid, ClaimType.Professional, 12.5m);

        var csv = ClaimReportBuilder.Csv(ClaimReportCatalog.Find(ClaimReportCatalog.Denials)!.Columns, [row]);
        var lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.StartsWith("ClaimId,ClaimNumber,Status,DenialReasonCode", lines[0]);
        Assert.Equal("c-1,\"'=HYPERLINK(\"\"x\"\")\",Denied,CO-50,\"Not medically necessary, per policy\",2026-10-01T00:00:00Z,,1234567890,Medicaid,Professional,12.5", lines[1]);
    }

    [Fact]
    public void Every_catalog_column_exists_on_its_row_type()
    {
        foreach (var id in new[] { ClaimReportCatalog.PendedInventory, ClaimReportCatalog.OpenAging })
            ClaimReportBuilder.Csv<ClaimInventoryRow>(ClaimReportCatalog.Find(id)!.Columns, []);
        ClaimReportBuilder.Csv<ClaimDenialRow>(ClaimReportCatalog.Find(ClaimReportCatalog.Denials)!.Columns, []);
        ClaimReportBuilder.Csv<ClaimTimelinessRow>(ClaimReportCatalog.Find(ClaimReportCatalog.ProcessingTimeliness)!.Columns, []);
    }
}

/// <summary>
/// The reports API through the real pipeline: reports:claims is required,
/// row-level runs are written to the claim access log with every claim they
/// returned (JSON and CSV alike), and runs are capped and say so.
/// </summary>
public class ClaimReportsApiTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;

    public ClaimReportsApiTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _factory.AccessAudit.Fail = false;
    }

    private HttpClient Client(string role, string user)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private static Claim Pended(string id) => new()
    {
        Id = id, TenantId = "test-tenant", ClaimNumber = "CN-" + id, MemberId = "MBR-" + id,
        Status = ClaimStatus.Pended, SubmittedDate = DateTime.UtcNow.AddDays(-40), BillingProviderNPI = "1234567890",
        PendDetails = new PendDetails { PendCode = "COB" }, TotalChargeAmount = 100m,
    };

    [Fact]
    public async Task The_catalog_lists_the_four_claims_reports()
    {
        var response = await Client(ChoRolePermissions.ClaimsSupervisor, "sup-1").GetAsync("/api/claims/reports");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { ClaimReportCatalog.PendedInventory, ClaimReportCatalog.OpenAging, ClaimReportCatalog.Denials, ClaimReportCatalog.ProcessingTimeliness },
            body.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task An_examiner_without_reports_claims_is_refused_and_the_refusal_is_logged()
    {
        var response = await Client(ChoRolePermissions.ClaimsExaminer, "exam-reports").GetAsync("/api/claims/reports/" + ClaimReportCatalog.PendedInventory);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var e = Assert.Single(_factory.AccessAudit.All, x => x.ActorId == "exam-reports");
        Assert.Equal((ClaimAccessActions.Report, ClaimAccessOutcomes.Denied), (e.Action, e.Outcome));
    }

    [Fact]
    public async Task A_pended_inventory_run_returns_rows_without_members_and_logs_every_claim()
    {
        var a = Pended("rpt-a-" + Guid.NewGuid().ToString("N")[..6]);
        var b = Pended("rpt-b-" + Guid.NewGuid().ToString("N")[..6]);
        _factory.ClaimRepository.GetReportRowsAsync(Arg.Is<ClaimCriteria>(c => c.Status == ClaimStatus.Pended && c.PendCode == "COB"),
                ClaimReportLimits.MaxRows, Arg.Any<CancellationToken>())
            .Returns((new[] { a, b }, 2));

        var response = await Client(ChoRolePermissions.ClaimsSupervisor, "sup-json").GetAsync($"/api/claims/reports/{ClaimReportCatalog.PendedInventory}?pendCode=COB");

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("MBR-", text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(body.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("sup-json", body.RootElement.GetProperty("runBy").GetString());
        Assert.Equal("COB", body.RootElement.GetProperty("parameters").GetProperty("pendCode").GetString());

        var e = Assert.Single(_factory.AccessAudit.All, x => x.ActorId == "sup-json");
        Assert.Equal(ClaimAccessActions.Report, e.Action);
        Assert.Equal(new[] { a.Id, b.Id }, e.ClaimIds);
        Assert.Equal(2, e.ResultCount);
    }

    [Fact]
    public async Task A_csv_run_is_a_file_and_still_logs_every_claim_and_flags_truncation()
    {
        var a = Pended("rpt-c-" + Guid.NewGuid().ToString("N")[..6]);
        _factory.ClaimRepository.GetReportRowsAsync(Arg.Is<ClaimCriteria>(c => c.Open && c.ReceivedOnOrBefore != null),
                ClaimReportLimits.MaxRows, Arg.Any<CancellationToken>())
            .Returns((new[] { a }, 5000));

        var response = await Client(ChoRolePermissions.ClaimsSupervisor, "sup-csv").GetAsync($"/api/claims/reports/{ClaimReportCatalog.OpenAging}?minAgeDays=31&format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1 of 5000", response.Headers.GetValues("X-Report-Truncated").Single());
        var lines = (await response.Content.ReadAsStringAsync()).TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Contains("31-45", lines[1]);

        var e = Assert.Single(_factory.AccessAudit.All, x => x.ActorId == "sup-csv");
        Assert.Equal(new[] { a.Id }, e.ClaimIds);
    }

    [Fact]
    public async Task Denials_reject_a_backwards_period()
    {
        var response = await Client(ChoRolePermissions.ClaimsSupervisor, "sup-bad").GetAsync(
            $"/api/claims/reports/{ClaimReportCatalog.Denials}?from=2026-10-01&to=2026-09-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Timeliness_returns_monthly_counts_and_is_not_a_claim_read()
    {
        var received = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        _factory.ClaimRepository.GetReceiptToAdjudicationAsync(Arg.Any<ClaimCriteria>(), ClaimReportLimits.MaxTimelinessClaims + 1, Arg.Any<CancellationToken>())
            .Returns(new List<(DateTime, DateTime?)> { (received, received.AddDays(10)), (received, null) });

        var response = await Client(ChoRolePermissions.ClaimsSupervisor, "sup-time").GetAsync(
            $"/api/claims/reports/{ClaimReportCatalog.ProcessingTimeliness}?from=2026-03-01&to=2026-05-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("2026-03", items[0].GetProperty("receiptMonth").GetString());
        Assert.Equal(2, items[0].GetProperty("received").GetInt32());
        Assert.Equal(1, items[0].GetProperty("within30Days").GetInt32());
        Assert.False(items[0].GetProperty("meets30DayStandard").GetBoolean());
        Assert.NotEmpty(body.RootElement.GetProperty("notes").EnumerateArray());
        Assert.DoesNotContain(_factory.AccessAudit.All, x => x.ActorId == "sup-time");
    }
}
