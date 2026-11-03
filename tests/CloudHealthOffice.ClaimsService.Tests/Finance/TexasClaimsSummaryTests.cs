using ClaimsService.Controllers;
using ClaimsService.Finance;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace CloudHealthOffice.ClaimsService.Tests.Finance;

/// <summary>HHSC's monthly Claims Summary Report (UMCM 5.24.1 v2.5) and its fixed-width text file.</summary>
public class TexasClaimsSummaryTests
{
    private static readonly DateOnly Month = new(2026, 10, 1);

    // Noon Central on the day, so the Texas calendar date is the day given.
    private static DateTime T(int month, int day) => new(2026, month, day, 17, 0, 0, DateTimeKind.Utc);

    private static ClaimsSummaryClaim C(
        string number, DateTime received, DateTime? adjudicated, ClaimStatus status, decimal paid = 0m,
        int version = 1, ClaimType form = ClaimType.Professional, string member = "M1", string? denial = null,
        string? pend = null, string npi = "1111111111", string[]? procs = null, string[]? revs = null, bool dup = false) =>
        new(number, version, member, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), form, received, adjudicated, status,
            paid, denial, pend, dup, npi, "11", procs ?? ["99213"], revs ?? []);

    private static readonly ExpectedRateCell[] Cells =
    [
        new("M1", new DateOnly(2026, 9, 1), "STAR-BEXAR"),
        new("M2", new DateOnly(2026, 9, 1), "SPLUS-BEXAR"),
        new("M3", new DateOnly(2026, 9, 1), "OTHER"),
    ];

    private static TexasClaimsSummaryRequest Request() => new()
    {
        ClaimsMonth = "2026-10",
        McoCode = "SUP",
        RateCellPrograms = new() { ["STAR-BEXAR"] = "01", ["SPLUS-BEXAR"] = "02" },
        ClaimTypeRules =
        [
            new() { ClaimType = "UR", ClaimForm = "Institutional", RevenueCodePrefixes = ["010"] },
            new() { ClaimType = "MT", ProcedureCodePrefixes = ["A01"] },
        ],
        DeficientPendCodes = ["MEDREVIEW"],
        AppealedClaimNumbers = ["C-APL"],
        CapitatedBillingNpis = ["9999999999"],
    };

    private static ClaimsSummaryFigures Line(TexasClaimsSummary r, string program, string type, string form) =>
        r.Lines.Single(l => l.Program == program && l.ClaimType == type && l.ClaimForm == form).Figures;

    [Fact]
    public void Layout_matches_hhsc_positions()
    {
        var at = TexasClaimsSummaryReport.Layout.ToDictionary(f => f.Name);
        at["ClaimForm"].Start.Should().Be(20);
        at["B.CleanPaidClaimsOver90"].Start.Should().Be(21);
        at["B.AppealedPaidClaimsOver90"].Start.Should().Be(108);
        at["B.AdjustedClaims"].Start.Should().Be(195);
        at["B.AverageProcessingDays"].End.Should().Be(285);
        at["C.CleanPaidClaimsOver90"].Start.Should().Be(286);
        at["D.AverageProcessingDays"].End.Should().Be(815);
        at["E.ElectronicPaidOver18Claims"].Start.Should().Be(816);
        at["E.NonElectronicPaidOver21Claims"].Start.Should().Be(874);
        at["E.InterestAmount"].End.Should().Be(953);
        at["F.Reason"].Start.Should().Be(1104);
        at["F.Date"].End.Should().Be(1278);
        at["G.Amount"].Start.Should().Be(1581);
        at["G.Date"].End.Should().Be(1603);
    }

    [Fact]
    public void Claims_are_counted_by_kind_and_days_from_receipt()
    {
        var adjudicated = new[]
        {
            C("C1", T(10, 1), T(10, 20), ClaimStatus.Paid, 100m),                       // 19 days
            C("C2", T(8, 1), T(10, 5), ClaimStatus.Approved, 50m),                     // 65 days
            C("C3", T(6, 1), T(10, 5), ClaimStatus.Paid, 25m),                         // 126 days
            C("C4", T(10, 1), T(10, 2), ClaimStatus.Denied, denial: "96"),
            C("C5", T(10, 1), T(10, 2), ClaimStatus.Deny, denial: "18"),
            C("C6", T(10, 1), T(10, 2), ClaimStatus.Denied, dup: true),
            C("C7", T(10, 1), T(10, 2), ClaimStatus.Denied, denial: "16"),
            C("C8", T(10, 1), T(10, 3), ClaimStatus.Paid, 40m, version: 2),
            C("C9", T(10, 1), T(10, 3), ClaimStatus.Voided, version: 2),
            C("C-APL", T(9, 1), T(10, 15), ClaimStatus.Paid, 70m, version: 2),        // 44 days
            C("C10", T(10, 1), T(10, 3), ClaimStatus.Paid, 0m, npi: "9999999999"),
        };
        var open = new[]
        {
            C("O1", T(10, 20), null, ClaimStatus.Pended, pend: "MEDREVIEW"),
            C("O2", T(10, 25), null, ClaimStatus.InAdjudication),
            C("O3", T(10, 25), T(11, 4), ClaimStatus.Paid, 10m),                        // adjudicated after the month: still open at its end
            C("O4", T(11, 2), null, ClaimStatus.Submitted),                            // received after the month
        };

        var r = TexasClaimsSummaryReport.Build(Month, Request(), adjudicated, open, Cells);

        r.StateFiscalYear.Should().Be(2027);
        var f = Line(r, "01", "AC", "C");
        f.CleanPaidClaims.Should().Equal(1, 1, 1);
        f.CleanPaidAmount.Should().Equal(25m, 50m, 100m);
        f.CleanDeniedClaims.Should().Equal(0, 0, 1);
        f.DuplicateClaims.Should().Be(2);
        f.DeficientDeniedClaims.Should().Be(1);
        f.AdjustedClaims.Should().Be(2);
        f.AdjustedPaid.Should().Be(40m);
        f.AppealedPaidClaims.Should().Equal(0, 1, 0);
        f.AppealedPaidAmount.Should().Equal(0m, 70m, 0m);
        f.CapitatedClaims.Should().Be(1);
        f.DeficientPendedClaims.Should().Be(1);
        f.OtherUnprocessedClaims.Should().Be(2);
        // (19 + 65 + 126 + 1 + 1 + 1 + 1 + 2 + 2 + 44 + 2) / 11 = 24
        f.AverageProcessingDays.Should().Be(24);
    }

    [Fact]
    public void Claim_type_rules_set_the_type_and_the_form()
    {
        var adjudicated = new[]
        {
            C("N1", T(10, 1), T(10, 8), ClaimStatus.Paid, 300m, form: ClaimType.Institutional, member: "M2", revs: ["0101"]),   // 7 days, NF bucket <=10
            C("N2", T(10, 1), T(10, 20), ClaimStatus.Paid, 300m, form: ClaimType.Institutional, member: "M2", revs: ["0101"]),  // 19 days, NF bucket 11-90
            C("H1", T(10, 1), T(10, 20), ClaimStatus.Paid, 900m, form: ClaimType.Institutional, member: "M2", revs: ["0450"]),
            C("T1", T(10, 1), T(10, 2), ClaimStatus.Paid, 30m, procs: ["A0130"]),
        };

        var r = TexasClaimsSummaryReport.Build(Month, Request(), adjudicated, [], Cells);

        Line(r, "02", "UR", "N").CleanPaidClaims.Should().Equal(0, 1, 1);
        Line(r, "02", "AC", "U").CleanPaidAmount.Should().Equal(0m, 0m, 900m);
        Line(r, "01", "MT", "C").CleanPaidClaims.Should().Equal(0, 0, 1);
    }

    [Fact]
    public void Claims_without_a_program_or_with_a_type_the_program_does_not_report_are_left_out()
    {
        var adjudicated = new[]
        {
            C("X1", T(10, 1), T(10, 2), ClaimStatus.Paid, 1m, member: "M3"),
            C("X2", T(10, 1), T(10, 2), ClaimStatus.Paid, 1m, member: "NOPE"),
            C("X3", T(10, 1), T(10, 2), ClaimStatus.Paid, 1m, member: ""),
            C("X4", T(10, 1), T(10, 2), ClaimStatus.Paid, 1m, form: ClaimType.Dental),   // STAR has no dental line
        };

        var r = TexasClaimsSummaryReport.Build(Month, Request(), adjudicated, [], Cells);

        r.Lines.Should().BeEmpty();
        r.Excluded.Select(e => e.Reason).Should().BeEquivalentTo(
            TexasClaimsSummaryReport.UnmappedRateCell, TexasClaimsSummaryReport.NotEnrolled,
            TexasClaimsSummaryReport.NoMember, TexasClaimsSummaryReport.TypeNotReported);
        r.Excluded.Single(e => e.Reason == TexasClaimsSummaryReport.TypeNotReported).Examples.Should().Equal("01/DT");
    }

    [Fact]
    public void Text_file_has_fixed_width_lines_in_hhsc_positions()
    {
        var request = Request();
        request.InterestPenalties = [new() { Program = "01", ClaimType = "AC", ClaimForm = "C", Claims = 2, Amount = 12.5m }];
        request.Pharmacy = [new() { Program = "01", ElectronicPaidWithin18Claims = 1200, ElectronicPaidWithin18Amount = 45000.255m }];
        request.Recoupments = [new() { Program = "01", FacilityName = "Bexar General", Reason = "99", ReasonOther = "Audit by state", Amount = 75000m, Date = new DateOnly(2026, 10, 9) }];
        request.AdvancedPayments = [new() { Program = "02", LastName = "Garza", Reason = "01", Amount = 60000m, Date = new DateOnly(2026, 10, 2) }];
        var r = TexasClaimsSummaryReport.Build(Month, request,
            [C("C1", T(10, 1), T(10, 20), ClaimStatus.Paid, 1234.5m)], [], Cells);

        var text = TexasClaimsSummaryReport.Text(r, "01");
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.Length == 1603);
        static string At(string line, int start, int width) => line.Substring(start - 1, width);

        At(lines[0], 1, 21).Should().Be("000001SUP10202701ACC0");
        At(lines[0], 65, 22).Should().Be("1      1234.50        ");
        At(lines[0], 259, 22).Should().Be("2      12.50          ");
        At(lines[0], 286, 10).Should().Be("          ");                      // NF and ADA sections blank

        At(lines[1], 1, 20).Should().Be("000002SUP10202701PHP");
        At(lines[1], 838, 22).Should().Be("1200   45000.26       ");
        At(lines[1], 21, 10).Should().Be("          ");

        At(lines[2], 1, 21).Should().Be("000003SUP10202701RC  ");              // claim form blank for a recoupment
        At(lines[2], 1054, 13).Should().Be("Bexar General");
        At(lines[2], 1104, 17).Should().Be("99Audit by state ");
        At(lines[2], 1256, 23).Should().Be("75000.00       10092026");

        var program2 = TexasClaimsSummaryReport.Text(r, "02").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        program2.Should().ContainSingle();
        At(program2[0], 16, 5).Should().Be("02AP ");
        At(program2[0], 1329, 5).Should().Be("Garza");
        At(program2[0], 1429, 2).Should().Be("01");
        TexasClaimsSummaryReport.FileName(r, "01").Should().Be("ClaimsSummary-SUP-01-SFY2027-10.txt");
    }

    [Fact]
    public void A_figure_too_large_for_its_field_is_refused()
    {
        var request = Request();
        request.RejectedClaims = [new() { Program = "01", ClaimType = "AC", ClaimForm = "C", Claims = 10_000_000 }];
        var r = TexasClaimsSummaryReport.Build(Month, request, [], [], Cells);

        var act = () => TexasClaimsSummaryReport.Text(r, "01");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_is_validated_against_hhsc_codes()
    {
        var request = new TexasClaimsSummaryRequest
        {
            ClaimsMonth = "2026-13",
            McoCode = "XYZ",
            RateCellPrograms = new() { ["A"] = "10" },
            ClaimTypeRules = [new() { ClaimType = "PH" }],
            Pharmacy = [new() { Program = "07" }],
            Recoupments = [new() { Program = "01", Reason = "99", Amount = 0m }],
        };

        var errors = request.Validate(out _);

        errors.Should().Contain(e => e.StartsWith("claimsMonth"));
        errors.Should().Contain(e => e.StartsWith("mcoCode"));
        errors.Should().Contain(e => e.Contains("'10' is not an HHSC program code"));
        errors.Should().Contain(e => e.StartsWith("claimTypeRules[0]"));
        errors.Should().Contain(e => e.Contains("program 07 does not report pharmacy"));
        errors.Should().Contain(e => e.Contains("reasonOther is required"));
        errors.Should().Contain(e => e.Contains("amount must be more than zero"));
        errors.Should().Contain(e => e.Contains("date is required"));
        errors.Should().Contain(e => e.Contains("last name or the facility name"));
    }

    [Fact]
    public async Task Text_endpoint_returns_the_program_file_and_lists_left_out_claims()
    {
        var repo = new Mock<IClaimRepository>();
        repo.Setup(r => r.GetClaimsSummaryAdjudicatedAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([C("C1", T(10, 1), T(10, 20), ClaimStatus.Paid, 10m), C("X1", T(10, 1), T(10, 2), ClaimStatus.Paid, 1m, member: "M3")]);
        repo.Setup(r => r.GetClaimsSummaryOpenAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var source = new Mock<IExpectedRateCellSource>();
        source.Setup(s => s.GetAsync("t1", "TX", new DateOnly(2026, 9, 1), Month, It.IsAny<CancellationToken>())).ReturnsAsync(Cells);
        var controller = new ClaimsFinanceController(repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var actor = Mock.Of<ICurrentActor>(a => a.TenantId == "t1" && a.UserId == "finance-1");

        var result = await controller.TexasClaimsSummaryText("01", Request(), source.Object, actor, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("ClaimsSummary-SUP-01-SFY2027-10.txt");
        file.FileContents.Length.Should().Be(1605);
        controller.Response.Headers["X-Report-Excluded"].ToString().Should().Contain(TexasClaimsSummaryReport.UnmappedRateCell);

        (await controller.TexasClaimsSummaryText("05", Request(), source.Object, actor, CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
        (await controller.TexasClaimsSummaryText("1", Request(), source.Object, actor, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }
}
