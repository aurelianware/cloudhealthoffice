using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Clients;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>
/// Expected capitation member-months built from coverage spans, the 834's
/// rate cell inputs and the plan's crosswalk. The categories and codes here
/// are made up; no state's real 834 layout is assumed.
/// </summary>
public class ExpectedMemberMonthBuilderTests
{
    private static readonly DateOnly Jan = new(2027, 1, 1);
    private static readonly DateOnly Mar = new(2027, 3, 1);

    private static RateCellInputSnapshot Snap(DateOnly from, string? riskGroup, DateOnly? dob = null, string? county = null, int version = 1, string? hd04 = null) =>
        new(from, version,
            riskGroup is null ? [] : [("RISK GROUP", riskGroup)],
            hd04 is null ? [] : [hd04],
            dob, county);

    private static RateCellCrosswalkRow Row(string cell, string? field = "category:Risk Group", string? value = null,
        int? ageMin = null, int? ageMax = null, string? county = null, string plan = "STAR") =>
        new(cell, plan, value is null ? null : field, value, ageMin, ageMax, county, null, null);

    private static Dictionary<string, IReadOnlyList<RateCellInputSnapshot>> Inputs(params (string Member, RateCellInputSnapshot[] Snaps)[] m) =>
        m.ToDictionary(x => x.Member, x => (IReadOnlyList<RateCellInputSnapshot>)x.Snaps, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Places_each_covered_month_by_category_age_and_county()
    {
        var crosswalk = new[]
        {
            Row("BEXAR-CHILD-1-5", value: "TANF-CHILD", ageMin: 1, ageMax: 5, county: "029"),
            Row("BEXAR-CHILD-6-14", value: "TANF-CHILD", ageMin: 6, ageMax: 14, county: "029"),
            Row("BEXAR-ADULT", value: "TANF-ADULT", county: "029"),
        };
        var coverage = new[]
        {
            new PlanCoverageSpan("KID", "STAR", new DateOnly(2026, 6, 1), null),
            new PlanCoverageSpan("MOM", "STAR", new DateOnly(2027, 1, 15), new DateOnly(2027, 2, 10)),
        };
        // KID turns 6 on Feb 1: January is the 1-5 cell, February and March 6-14.
        var inputs = Inputs(
            ("KID", [Snap(new DateOnly(2026, 6, 1), "TANF-CHILD", new DateOnly(2021, 2, 1), "029")]),
            ("MOM", [Snap(new DateOnly(2027, 1, 15), "TANF-ADULT", new DateOnly(1990, 5, 5), "029")]));

        var build = ExpectedMemberMonthBuilder.Build(Jan, Mar, coverage, inputs, crosswalk);

        build.Exceptions.Should().BeEmpty();
        build.MemberMonths.Should().BeEquivalentTo(new[]
        {
            new ExpectedMemberMonth("KID", Jan, "BEXAR-CHILD-1-5", 31),
            new ExpectedMemberMonth("KID", new DateOnly(2027, 2, 1), "BEXAR-CHILD-6-14", 28),
            new ExpectedMemberMonth("KID", Mar, "BEXAR-CHILD-6-14", 31),
            new ExpectedMemberMonth("MOM", Jan, "BEXAR-ADULT", 17),
            new ExpectedMemberMonth("MOM", new DateOnly(2027, 2, 1), "BEXAR-ADULT", 10),
        });
    }

    [Fact]
    public void A_later_834_changes_the_cell_from_its_effective_date()
    {
        var crosswalk = new[] { Row("CHILD", value: "TANF-CHILD"), Row("SSI", value: "SSI") };
        var coverage = new[] { new PlanCoverageSpan("M1", "STAR", new DateOnly(2026, 1, 1), null) };
        var inputs = Inputs(("M1", [Snap(new DateOnly(2026, 1, 1), "TANF-CHILD"), Snap(new DateOnly(2027, 2, 1), "SSI", version: 2)]));

        var build = ExpectedMemberMonthBuilder.Build(Jan, Mar, coverage, inputs, crosswalk);

        build.MemberMonths.Select(m => m.RateCell).Should().Equal("CHILD", "SSI", "SSI");
    }

    [Fact]
    public void Matches_on_hd04_and_plan_only_rows()
    {
        var crosswalk = new[]
        {
            Row("STAR-KIDS", field: "hd04", value: "STAR KIDS BEXAR", plan: "KIDS"),
            Row("CHIP-ALL", field: null, value: null, plan: "CHIP"),
        };
        var coverage = new[]
        {
            new PlanCoverageSpan("A", "KIDS", Jan, null),
            new PlanCoverageSpan("B", "CHIP", Jan, null),
        };
        var inputs = Inputs(("A", [Snap(Jan, null, hd04: "star kids bexar")]), ("B", [Snap(Jan, null, hd04: "CHIP")]));

        var build = ExpectedMemberMonthBuilder.Build(Jan, Jan, coverage, inputs, crosswalk);

        build.MemberMonths.Select(m => (m.MemberId, m.RateCell)).Should().BeEquivalentTo(new[] { ("A", "STAR-KIDS"), ("B", "CHIP-ALL") });
    }

    [Fact]
    public void Never_guesses_a_rate_cell()
    {
        var crosswalk = new[]
        {
            Row("ADULT-A", value: "ADULT"), Row("ADULT-B", value: "ADULT"),
            Row("CHILD", value: "CHILD", ageMin: 0, ageMax: 18),
        };
        var coverage = new[]
        {
            new PlanCoverageSpan("UNKNOWN", "STAR", Jan, null),
            new PlanCoverageSpan("TWO", "STAR", Jan, null),
            new PlanCoverageSpan("NODOB", "STAR", Jan, null),
            new PlanCoverageSpan("NOINPUT", "STAR", Jan, null),
            new PlanCoverageSpan("LATE", "STAR", Jan, null),
            new PlanCoverageSpan("BOTH", "STAR", Jan, new DateOnly(2027, 1, 15)),
            new PlanCoverageSpan("BOTH", "OTHER", new DateOnly(2027, 1, 16), null),
        };
        var inputs = Inputs(
            ("UNKNOWN", [Snap(Jan, "DUAL")]),
            ("TWO", [Snap(Jan, "ADULT")]),
            ("NODOB", [Snap(Jan, "CHILD")]),
            ("LATE", [Snap(new DateOnly(2027, 1, 20), "ADULT")]),
            ("BOTH", [Snap(Jan, "ADULT")]));

        var build = ExpectedMemberMonthBuilder.Build(Jan, Jan, coverage, inputs, crosswalk);

        build.MemberMonths.Should().BeEmpty();
        build.Exceptions.ToDictionary(e => e.MemberId, e => e.Issue).Should().BeEquivalentTo(new Dictionary<string, MemberMonthBuildIssue>
        {
            ["UNKNOWN"] = MemberMonthBuildIssue.NoMatch,
            ["TWO"] = MemberMonthBuildIssue.Ambiguous,
            ["NODOB"] = MemberMonthBuildIssue.NoDateOfBirth,
            ["NOINPUT"] = MemberMonthBuildIssue.NoInputs,
            ["LATE"] = MemberMonthBuildIssue.NoInputs,
            ["BOTH"] = MemberMonthBuildIssue.MultiplePlans,
        });
    }

    [Fact]
    public void Crosswalk_csv_reads_every_column_and_refuses_bad_rows()
    {
        var rows = StateCapitationCsv.ReadCrosswalk(
            "rate_cell,plan_id,field,value,age_min,age_max,county,effective_from,effective_to\n" +
            "LA-CHILD,MEDI-CAL,category:AID CODE,3T,0,18,19,2027-01,2027-12\n" +
            "LA-ADULT,MEDI-CAL,,,19,,19,,\n", out var errors);

        errors.Should().BeEmpty();
        rows[0].Should().Be(new RateCellCrosswalkRow("LA-CHILD", "MEDI-CAL", "category:AID CODE", "3T", 0, 18, "19",
            new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 1)));
        rows[1].Field.Should().BeNull();

        StateCapitationCsv.ReadCrosswalk("rate_cell,field,value,age_min,age_max\nX,aid,3T,,\nY,hd04,,,\nZ,,,9,2\n", out var bad);
        bad.Should().HaveCount(3);
    }

    // ── Endpoint ─────────────────────────────────────────────────────────

    private sealed class Store : IStateCapitationStore
    {
        public List<StoredCrosswalkRow> Crosswalk { get; } = new();
        public List<StoredExpectedMonth> Expected { get; } = new();

        public Task<int> ReplaceCrosswalkAsync(string state, IReadOnlyList<StoredCrosswalkRow> rows)
        {
            Crosswalk.RemoveAll(r => r.State == state);
            foreach (var r in rows) { r.State = state; Crosswalk.Add(r); }
            return Task.FromResult(rows.Count);
        }

        public Task<IReadOnlyList<RateCellCrosswalkRow>> GetCrosswalkAsync(string state) =>
            Task.FromResult<IReadOnlyList<RateCellCrosswalkRow>>(Crosswalk.Where(r => r.State == state).OrderBy(r => r.Line).Select(r => r.ToRow()).ToList());

        public Task<int> ReplaceExpectedAsync(string state, IReadOnlyCollection<DateOnly> months, IReadOnlyList<StoredExpectedMonth> rows)
        {
            Expected.RemoveAll(e => e.State == state && months.Contains(DateOnly.FromDateTime(e.Month)));
            foreach (var r in rows) { r.State = state; Expected.Add(r); }
            return Task.FromResult(rows.Count);
        }

        public Task<IReadOnlyList<ExpectedMemberMonth>> GetExpectedAsync(string state, DateOnly fromMonth, DateOnly toMonth) =>
            Task.FromResult<IReadOnlyList<ExpectedMemberMonth>>(Expected.Where(e => e.State == state).Select(e => e.ToExpected()).ToList());

        public Task<int> UpsertRatesAsync(string state, IReadOnlyList<StoredStateRate> rates) => Task.FromResult(0);
        public Task<IReadOnlyList<StateCapitationRate>> GetRatesAsync(string state) => Task.FromResult<IReadOnlyList<StateCapitationRate>>([]);
        public Task<bool> TryAddPaymentAsync(StoredStatePayment payment) => Task.FromResult(true);
        public Task<IReadOnlyList<RemittanceAdvice>> GetPaymentsAsync(string state) => Task.FromResult<IReadOnlyList<RemittanceAdvice>>([]);
    }

    private sealed class Coverage(Dictionary<string, List<CoverageDto>> byPlan, bool fail = false) : ICoverageServiceClient
    {
        public List<string> PlansRead { get; } = new();
        public Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<CoverageDto>> GetAllCoveragesByPlanAsync(string tenantId, string planId, CancellationToken cancellationToken = default)
        {
            if (fail) throw new HttpRequestException("coverage-service down");
            PlansRead.Add(planId);
            return Task.FromResult(byPlan.GetValueOrDefault(planId) ?? new List<CoverageDto>());
        }
    }

    private sealed class Inputs834(IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>> all) : IRateCellInputSource
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>>> GetAllAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult(all);
    }

    private readonly Store _store = new();

    private ExpectedMemberMonthsController Controller(ICoverageServiceClient coverage, IRateCellInputSource inputs, string? body = null)
    {
        var http = new DefaultHttpContext();
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
        }
        var actor = Mock.Of<ICurrentActor>(a => a.UserId == "finance-1" && a.TenantId == "t");
        return new ExpectedMemberMonthsController(_store, coverage, inputs, actor, NullLogger<ExpectedMemberMonthsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Build_dry_run_reports_then_apply_replaces_the_months()
    {
        var coverage = new Coverage(new()
        {
            ["MEDI-CAL"] =
            [
                new CoverageDto { MemberId = "91000001A", PlanId = "MEDI-CAL", EffectiveDate = new DateTime(2026, 7, 1) },
                new CoverageDto { MemberId = "91000002B", PlanId = "MEDI-CAL", EffectiveDate = new DateTime(2026, 7, 1) },
                new CoverageDto { MemberId = "VOID", PlanId = "MEDI-CAL", EffectiveDate = new DateTime(2027, 1, 5), TerminationDate = new DateTime(2027, 1, 4) },
            ],
        });
        var inputs = new Inputs834(Inputs(
            ("91000001A", [Snap(new DateOnly(2026, 7, 1), null, new DateOnly(1988, 1, 1)) with { Categories = [("AID CODE", "M1")] }]),
            ("91000002B", [Snap(new DateOnly(2026, 7, 1), null) with { Categories = [("AID CODE", "ZZ")] }])));

        var loaded = Json(await Controller(coverage, inputs,
            "rate_cell,plan_id,field,value\nLA-ACA-EXP,MEDI-CAL,category:AID CODE,M1\n").LoadCrosswalk("ca"));
        loaded.GetProperty("rowsLoaded").GetInt32().Should().Be(1);
        _store.Expected.Add(new StoredExpectedMonth { State = "CA", MemberId = "OLD", Month = new DateTime(2027, 1, 1), RateCell = "X" });

        var dry = Json(await Controller(coverage, inputs).Build("CA", "2027-01", "2027-01"));
        dry.GetProperty("memberMonths").GetInt32().Should().Be(1);
        dry.GetProperty("exceptionCount").GetInt32().Should().Be(1);
        dry.GetProperty("exceptions")[0].GetProperty("issue").GetString().Should().Be("NoMatch");
        _store.Expected.Should().ContainSingle(e => e.MemberId == "OLD", "a dry run changes nothing");

        var applied = Json(await Controller(coverage, inputs).Build("CA", "2027-01", "2027-01", apply: true));
        applied.GetProperty("memberMonthsLoaded").GetInt32().Should().Be(1);
        _store.Expected.Should().ContainSingle().Which.Should().Match<StoredExpectedMonth>(e =>
            e.MemberId == "91000001A" && e.RateCell == "LA-ACA-EXP" && e.SourceFile == "834-build" && e.LoadedBy == "finance-1");
        coverage.PlansRead.Should().OnlyContain(p => p == "MEDI-CAL");
    }

    [Fact]
    public async Task Build_refuses_without_a_crosswalk_or_when_coverage_cannot_be_read()
    {
        var inputs = new Inputs834(Inputs());
        (await Controller(new Coverage(new()), inputs).Build("TX", "2027-01", "2027-01"))
            .Should().BeOfType<ConflictObjectResult>();

        (await Controller(new Coverage(new()), inputs, "rate_cell,field,value\nX,hd04,STAR\n").LoadCrosswalk("TX"))
            .Should().BeOfType<BadRequestObjectResult>("every row needs a plan to list coverage by");

        await Controller(new Coverage(new()), inputs, "rate_cell,plan_id\nX,STAR\n").LoadCrosswalk("TX");
        var down = await Controller(new Coverage(new(), fail: true), inputs).Build("TX", "2027-01", "2027-01", apply: true);
        down.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        _store.Expected.Should().BeEmpty();
    }
}
