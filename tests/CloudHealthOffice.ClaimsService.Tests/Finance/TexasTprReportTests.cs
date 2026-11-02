using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Finance;
using ClaimsService.Models.Tpl;
using ClaimsService.Repositories;
using ClaimsService.Services.Tpl;
using CloudHealthOffice.Infrastructure.Reporting;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace CloudHealthOffice.ClaimsService.Tests.Finance;

/// <summary>
/// HHSC's TPL/TPR Report (UMCM 5.3.4.1, instructions 5.3.4.2): cost
/// avoidance, credits and recoveries by program, plan code and month, and
/// the state's workbook filled in.
/// </summary>
public class TexasTprReportTests
{
    private static DateOnly M(int year, int month) => new(year, month, 1);
    private static DateTime D(int year, int month, int day = 15) => new(year, month, day, 18, 0, 0, DateTimeKind.Utc);

    private static readonly ExpectedRateCell[] Cells =
    [
        new("M1", M(2026, 8), "STAR-BEXAR"),
        new("M1", M(2026, 9), "STAR-BEXAR"),
        new("M1", M(2026, 10), "STAR-BEXAR"),
        new("M2", M(2026, 9), "SP-BEXAR"),
        new("M3", M(2026, 9), "STAR-OTHER"),   // not mapped
    ];

    private static TexasTprRequest Request() => new()
    {
        StateFiscalYear = 2027,
        Quarter = 1,
        Contractor = "Lone Star Health Plan",
        McoCode = "ls1",
        SubmissionDate = M(2026, 12),
        RateCellPlanCodes = new()
        {
            ["STAR-BEXAR"] = new TexasTprPlanCode { Program = "star", PlanCode = "4F" },
            ["SP-BEXAR"] = new TexasTprPlanCode { Program = "STAR+PLUS", PlanCode = "45" },
        },
        TortRecoveries = [new TexasTprTortRecovery { Program = "STAR", PlanCode = "4F", Month = "2026-10", Amount = 1200m }],
    };

    private static IReadOnlyDictionary<string, (string, string)> PlanCodes()
    {
        Request().Validate(false, out var p).Should().BeEmpty();
        return p;
    }

    private static TplReportClaim Avoided(string number, int version, string member, DateTime adjudicated, decimal? allowed) =>
        new(number, version, member, D(2026, 9, 1), adjudicated, true, "CostAvoided", allowed, 0m);

    private static TplReportClaim Credited(string number, int version, string member, DateTime adjudicated, decimal otherPaid) =>
        new(number, version, member, D(2026, 9, 1), adjudicated, false, "Coordinated", null, otherPaid);

    private static TplRecoveryCase Case(string member, TplRecoveryOrigin origin, params TplRecoveryEvent[] events) => new()
    {
        TenantId = "t1", ClaimNumber = "R-" + member, MemberId = member, State = "TX", Origin = origin,
        ServiceDateFrom = D(2026, 9, 2), Events = events.ToList(),
    };

    private static TplRecoveryEvent Paid(DateTime at, decimal amount, TplRecoverySource? source = null) =>
        new() { Type = TplRecoveryEventType.PaymentReceived, At = at, Amount = amount, Source = source };

    private static decimal Amount(TexasTpr r, string program, string plan, DateOnly month, TexasTprLine line) =>
        r.Amounts.SingleOrDefault(a => a.Program == program && a.PlanCode == plan && a.Month == month && a.Line == line)?.Amount ?? 0m;

    [Fact]
    public void Amounts_go_by_plan_code_line_and_month_of_avoidance_or_recovery()
    {
        var claims = new[]
        {
            Avoided("C1", 1, "M1", D(2026, 9), 250m),
            Avoided("C1", 2, "M1", D(2026, 10), 250m),    // the same claim again: counted once
            Avoided("C2", 1, "M2", D(2026, 10), 90m),
            Avoided("C3", 1, "M1", D(2026, 10), null),    // not priced
            Avoided("C4", 1, "M3", D(2026, 9), 40m),      // rate cell not mapped
            Credited("C5", 1, "M1", D(2026, 11), 60m),
            Avoided("C6", 1, "M1", D(2026, 12), 999m),    // after Q1
        };
        var cases = new[]
        {
            Case("M1", TplRecoveryOrigin.PayAndChase, Paid(D(2026, 10), 300m)),                          // inferred: carrier
            Case("M1", TplRecoveryOrigin.Retroactive, Paid(D(2026, 11), 75m, TplRecoverySource.Provider)),
            Case("M1", TplRecoveryOrigin.Retroactive, Paid(D(2026, 11), 20m, TplRecoverySource.Carrier)),
        };

        var r = TexasTprReport.Build(2027, 1, claims, cases, Cells, PlanCodes(), Request().TortRecoveries);

        r.From.Should().Be(M(2026, 9));
        r.Through.Should().Be(M(2026, 11));
        r.Plans.Should().Equal(new TexasTprPlan("STAR", "4F"), new TexasTprPlan("STAR+PLUS", "45"));
        Amount(r, "STAR", "4F", M(2026, 9), TexasTprLine.DeniedClaims).Should().Be(250m);
        Amount(r, "STAR", "4F", M(2026, 10), TexasTprLine.DeniedClaims).Should().Be(0m);
        Amount(r, "STAR+PLUS", "45", M(2026, 10), TexasTprLine.DeniedClaims).Should().Be(90m);
        Amount(r, "STAR", "4F", M(2026, 11), TexasTprLine.OtherInsuranceCredits).Should().Be(60m);
        Amount(r, "STAR", "4F", M(2026, 10), TexasTprLine.OtherInsuranceRecoveries).Should().Be(300m);
        Amount(r, "STAR", "4F", M(2026, 11), TexasTprLine.OtherInsuranceRecoveries).Should().Be(20m);
        Amount(r, "STAR", "4F", M(2026, 11), TexasTprLine.ProviderRefundsAndRecoupments).Should().Be(75m);
        Amount(r, "STAR", "4F", M(2026, 10), TexasTprLine.TortRecoveries).Should().Be(1200m);
        r.Amounts.Should().NotContain(a => a.Amount == 999m);
        r.Excluded.Single(e => e.Reason == TexasTprReport.NotPriced).Examples.Should().Equal("C3");
        r.Excluded.Single(e => e.Reason == TexasTprReport.NoPlanCode).Amount.Should().Be(40m);
        r.Notes.Should().Contain(n => n.StartsWith("1 recovery payment(s) had no source recorded"));
    }

    [Fact]
    public void Months_are_texas_months()
    {
        // 03:00 UTC on 1 October is still 30 September in Texas.
        TexasTprReport.TexasMonth(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc)).Should().Be(M(2026, 9));
        var (from, before) = TexasTprReport.Window(2027, 2);
        from.Should().Be(new DateTime(2026, 9, 1, 5, 0, 0, DateTimeKind.Utc));
        before.Should().Be(new DateTime(2027, 3, 1, 6, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_request_with_bad_codes_or_no_contractor_for_the_workbook_is_refused()
    {
        var request = Request();
        request.Contractor = null;
        request.McoCode = "TOOLONG";
        request.RateCellPlanCodes["X"] = new TexasTprPlanCode { Program = "STAR Medicaid", PlanCode = "4F" };
        request.TortRecoveries.Add(new TexasTprTortRecovery { Program = "STAR", PlanCode = "4F", Month = "October" });

        request.Validate(forWorkbook: true, out _).Should().HaveCount(4);
    }

    [Fact]
    public void The_file_name_follows_hhsc()
    {
        var r = TexasTprReport.Build(2027, 1, [], [], Cells, PlanCodes(), []);
        TexasTprReport.FileName(r, "ls1").Should().Be("LS1TPRQ011126.xlsx");
        TexasTprReport.FileName(r, null).Should().Be("TPR-SFY2027-Q1.xlsx");
    }

    // ---- The workbook -------------------------------------------------------

    private static readonly string[] MonthNames =
        ["September", "October", "November", "December", "January", "February", "March", "April", "May", "June", "July", "August"];

    /// <summary>
    /// A small workbook laid out like HHSC's: a header in A2 to A5, then per
    /// month a block with the plan code row and the five lines, three plan
    /// code columns and a Total column, one sheet per program given.
    /// </summary>
    internal static byte[] Template(string[] programs, bool dropTort = false)
    {
        string Sheet()
        {
            var rows = new SortedDictionary<int, List<string>>();
            void Text(int row, string col, string text) =>
                (rows.TryGetValue(row, out var l) ? l : rows[row] = []).Add($"<c r=\"{col}{row}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>");
            void Formula(int row, string col, string f) =>
                (rows.TryGetValue(row, out var l) ? l : rows[row] = []).Add($"<c r=\"{col}{row}\"><f>{f}</f><v>7</v></c>");

            Text(1, "A", "Third Party Liability and Recoveries Report");
            foreach (var (row, label) in new[] { (2, "Contractor:"), (3, "SFY:"), (4, "Quarter:"), (5, "Date Submitted:") })
                Text(row, "A", label);
            for (var i = 0; i < 12; i++)
            {
                var r = 6 + i * 11;
                Text(r, "A", $"Q{i / 3 + 1}");
                Text(r, "B", MonthNames[i]);
                foreach (var c in new[] { "C", "D", "E" }) Text(r, c, "Plan Code");
                Text(r, "F", "Total");
                Text(r + 1, "B", "Cost Avoidance");
                Text(r + 2, "B", "Denied Claims");
                Text(r + 3, "B", "Direct Savings");
                Text(r + 4, "B", "Other Insurance Credits");
                Text(r + 5, "B", "Recoveries");
                Text(r + 6, "B", "Other Insurance Recoveries");
                Text(r + 7, "B", "Provider Refunds & Recoupments");
                if (!dropTort) Text(r + 8, "B", "Tort Recoveries");
                for (var off = 2; off <= 8; off++) Formula(r + off, "F", $"SUM(C{r + off}:E{r + off})");
            }
            static int ColumnOf(string cell) => cell[7] - 'A';
            return string.Concat(rows.Select(kv => $"<row r=\"{kv.Key}\">{string.Concat(kv.Value.OrderBy(ColumnOf))}</row>"));
        }

        string Cert()
        {
            var cells = new[] { (2, ""), (3, "MCO Name"), (4, ""), (5, "MCO Plan Code Numbers"), (6, ""), (7, "Document Name"), (8, ""), (9, "Date of Submission") };
            return string.Concat(cells.Where(c => c.Item2 != "").Select(c =>
                $"<row r=\"{c.Item1}\"><c r=\"B{c.Item1}\" t=\"inlineStr\"><is><t>{c.Item2}</t></is></c></row>"));
        }

        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var names = programs.Append("Certification").ToArray();
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["xl/workbook.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook {ns}><sheets>"
                + string.Concat(names.Select((n, i) => $"<sheet name=\"{SecurityElement.Escape(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + "</Relationships>",
        };
        for (var i = 0; i < names.Length; i++)
            parts[$"xl/worksheets/sheet{i + 1}.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet {ns}><sheetData>{(names[i] == "Certification" ? Cert() : Sheet())}</sheetData></worksheet>";

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, xml) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(xml));
            }
        return output.ToArray();
    }

    private static TexasTpr Sample() => TexasTprReport.Build(2027, 1,
        [Avoided("C1", 1, "M1", D(2026, 9), 250m), Avoided("C2", 1, "M2", D(2026, 10), 90m)],
        [], Cells, PlanCodes(), Request().TortRecoveries);

    [Fact]
    public void The_layout_is_read_from_the_sheets_own_labels()
    {
        var (layout, problems) = TexasTprReport.ReadLayout(XlsxTemplate.Open(Template(["STAR"])), "STAR");

        problems.Should().BeEmpty();
        layout!.MonthBlockRows[9].Should().Be(6);
        layout.MonthBlockRows[8].Should().Be(6 + 11 * 11);
        layout.PlanCodeColumns.Should().Equal("C", "D", "E");
        layout.PlanCodeRow.Should().Be(7);
    }

    [Fact]
    public void Filling_writes_the_header_plan_codes_every_month_through_the_quarter_and_the_certification()
    {
        var workbook = XlsxTemplate.Open(Template(["STAR", "STAR+PLUS"]));
        var report = Sample();
        var (layouts, problems) = TexasTprReport.ReadLayouts(workbook, report);
        problems.Should().BeEmpty();

        var filled = XlsxTemplate.Open(TexasTprReport.Fill(workbook, layouts, report, "Lone Star Health Plan", M(2026, 12)));

        filled.Text("STAR", "A2").Should().Be("Contractor: Lone Star Health Plan");
        filled.Text("STAR", "A3").Should().Be("SFY: 2027");
        filled.Text("STAR", "A4").Should().Be("Quarter: Q1");
        filled.Text("STAR", "A5").Should().Be("Date Submitted: 12/01/2026");
        filled.Text("STAR", "C7").Should().Be("4F");
        filled.Text("STAR", "C8").Should().Be("250");          // September denied claims
        filled.Text("STAR", "C10").Should().Be("0");           // September credits: zero, not blank
        filled.Text("STAR", "C25").Should().Be("1200");        // October (row 17) tort recoveries
        filled.Text("STAR", "C36").Should().Be("0");           // November (row 28) tort recoveries
        filled.Text("STAR", "C41").Should().BeNull();          // December (row 39) is after Q1: left alone
        filled.Text("STAR", "D8").Should().BeNull();           // no second STAR plan code
        filled.Text("STAR+PLUS", "C19").Should().Be("90");     // October denied claims
        filled.Formula("STAR", "F8").Should().Be("SUM(C8:E8)");
        filled.Text("STAR", "F8").Should().BeNull();           // stale total dropped
        filled.Text("Certification", "B2").Should().Be("Lone Star Health Plan");
        filled.Text("Certification", "B4").Should().Be("4F, 45");
        filled.Text("Certification", "B6").Should().Be("TPL/TPR Report for Quarter 1, SFY 2027");
        filled.Text("Certification", "B8").Should().Be("12/01/2026");
    }

    [Fact]
    public void A_template_missing_a_program_sheet_or_a_line_is_refused()
    {
        TexasTprReport.ReadLayouts(XlsxTemplate.Open(Template(["STAR"])), Sample()).Problems
            .Should().ContainSingle().Which.Should().Contain("STAR+PLUS");
        TexasTprReport.ReadLayout(XlsxTemplate.Open(Template(["STAR"], dropTort: true)), "STAR").Problems
            .Should().HaveCount(12).And.AllSatisfy(p => p.Should().Contain("Tort Recoveries"));
    }

    // ---- The endpoints ------------------------------------------------------

    private static readonly ICurrentActor Actor = Mock.Of<ICurrentActor>(a => a.TenantId == "t1" && a.UserId == "finance-1");

    private static (ClaimsFinanceController, Mock<IExpectedRateCellSource>, Mock<ITplRecoveryRepository>) Controller(
        TplReportClaim[] claims, TplRecoveryCase[] cases)
    {
        var repo = new Mock<IClaimRepository>();
        repo.Setup(r => r.GetTplReportClaimsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(claims);
        var recoveries = new Mock<ITplRecoveryRepository>();
        recoveries.Setup(r => r.ListAsync("t1", It.Is<TplRecoveryQuery>(q => q.State == "TX"), It.IsAny<CancellationToken>())).ReturnsAsync(cases);
        var source = new Mock<IExpectedRateCellSource>();
        source.Setup(s => s.GetAsync("t1", "TX", It.IsAny<DateOnly>(), M(2026, 11), It.IsAny<CancellationToken>())).ReturnsAsync(Cells);
        var controller = new ClaimsFinanceController(repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, source, recoveries);
    }

    [Fact]
    public async Task The_workbook_endpoint_fills_the_template_and_reads_rate_cells_back_to_the_earliest_service_month()
    {
        var early = Avoided("C9", 1, "M1", D(2026, 9), 30m) with { ServiceDateFrom = D(2026, 8, 20) };
        var (controller, source, recoveries) = Controller([Avoided("C1", 1, "M1", D(2026, 9), 250m), early], []);
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Template(["STAR", "STAR+PLUS"]);

        var result = await controller.TexasTprWorkbook(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "template", "5-3-4-1.xlsx"), json, source.Object, recoveries.Object, Actor, default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("LS1TPRQ011126.xlsx");
        XlsxTemplate.Open(file.FileContents).Text("STAR", "C8").Should().Be("280");
        source.Verify(s => s.GetAsync("t1", "TX", M(2026, 8), M(2026, 11), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task The_workbook_endpoint_refuses_a_template_without_the_program_sheets()
    {
        var (controller, source, recoveries) = Controller([], []);
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Template(["STAR"]);

        var result = await controller.TexasTprWorkbook(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "template", "x.xlsx"), json, source.Object, recoveries.Object, Actor, default);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task The_json_endpoint_says_when_the_rate_cells_cannot_be_read()
    {
        var (controller, source, recoveries) = Controller([], []);
        source.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExpectedRateCellsUnavailableException("premium-billing-service answered 503"));

        var result = await controller.TexasTprFigures(Request(), source.Object, recoveries.Object, Actor, default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }
}
