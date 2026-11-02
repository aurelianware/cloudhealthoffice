using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Finance;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Reporting;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace CloudHealthOffice.ClaimsService.Tests.Finance;

/// <summary>
/// HHSC's STAR Claims Lag Report (UMCM 5.6.2.11, instructions 5.6.2.4):
/// payments by risk group, month of service and month of payment, and the
/// state's workbook filled in.
/// </summary>
public class TexasClaimsLagTests
{
    private static DateOnly M(int year, int month) => new(year, month, 1);
    private static DateTime D(int year, int month, int day = 15) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, string> Mapping = new()
    {
        ["STAR-BEXAR-TANF-ADULT"] = "TANF-ADULT",
        ["STAR-BEXAR-PREG"] = "Pregnant Woman",
        ["STAR-BEXAR-U1"] = "UNDER-1",
    };

    private static readonly ExpectedRateCell[] Cells =
    [
        new("M1", M(2026, 9), "STAR-BEXAR-TANF-ADULT"),
        new("M1", M(2026, 10), "STAR-BEXAR-PREG"),       // became pregnant: October service is Pregnant Woman
        new("M2", M(2026, 9), "STAR-BEXAR-U1"),
        new("M3", M(2026, 9), "STARPLUS-BEXAR-OTHER"),   // another program
    ];

    private static IReadOnlyDictionary<string, string> Groups()
    {
        new TexasClaimsLagRequest { StateFiscalYear = 2027, Quarter = 1, RateCellRiskGroups = Mapping }.Validate(false, out var g);
        return g;
    }

    [Fact]
    public void Payments_go_by_risk_group_month_of_service_and_month_of_payment_with_adjustments_in_their_own_month()
    {
        var versions = new[]
        {
            new ClaimPaymentVersion("C1", 1, D(2026, 9), D(2026, 10), 100m, false, "M1"),
            new ClaimPaymentVersion("C1", 2, D(2026, 9), D(2026, 11), 80m, false, "M1"),   // recoupment of 20 in November
            new ClaimPaymentVersion("C2", 1, D(2026, 10), D(2026, 10), 300m, false, "M1"),
            new ClaimPaymentVersion("C3", 1, D(2026, 9), D(2026, 9), 50m, false, "M2"),
            new ClaimPaymentVersion("C4", 1, D(2026, 9), D(2026, 9), 70m, false, "M3"),    // STAR+PLUS: left out
            new ClaimPaymentVersion("C5", 1, D(2026, 9), D(2026, 10), 40m, false, "M9"),   // no expected month
            new ClaimPaymentVersion("C6", 1, D(2026, 9), D(2026, 12), 999m, false, "M1"),  // paid after Q1
        };

        var lag = TexasClaimsLagReport.Build(2027, 1, M(2026, 9), LagTriangle.Increments(versions), Cells, Groups());

        lag.Through.Should().Be(M(2026, 11));
        lag.Groups.Single(g => g.Code == "TANF-ADULT").Cells.Should().Equal(
            new TexasLagCell(M(2026, 9), M(2026, 10), 100m),
            new TexasLagCell(M(2026, 9), M(2026, 11), -20m));
        lag.Groups.Single(g => g.Code == "PREGNANT-WOMAN").Cells.Should().Equal(new TexasLagCell(M(2026, 10), M(2026, 10), 300m));
        lag.Groups.Single(g => g.Code == "UNDER-1").Total.Should().Be(50m);
        lag.Total.Should().Be(430m);
        lag.Excluded.Should().HaveCount(2);
        lag.Excluded.Single(e => e.Reason == TexasClaimsLagReport.UnmappedRateCell).Examples.Should().Equal("STARPLUS-BEXAR-OTHER");
        lag.Excluded.Single(e => e.Reason == TexasClaimsLagReport.NotEnrolled).Amount.Should().Be(40m);
        lag.Groups.Select(g => g.Title).Should().Equal(TexasStarRiskGroups.All.Select(g => g.Title));
    }

    [Theory]
    [InlineData(1, 2026, 11)]
    [InlineData(2, 2027, 2)]
    [InlineData(3, 2027, 5)]
    [InlineData(4, 2027, 8)]
    public void Quarters_end_in_november_february_may_and_august(int quarter, int year, int month) =>
        TexasClaimsLagReport.QuarterEnd(2027, quarter).Should().Be(M(year, month));

    [Fact]
    public void Validation_names_bad_groups_and_missing_header_fields()
    {
        var request = new TexasClaimsLagRequest
        {
            StateFiscalYear = 2027, Quarter = 5,
            RateCellRiskGroups = new() { ["X"] = "Adults" },
        };

        var errors = request.Validate(forWorkbook: true, out _);

        errors.Should().Contain(e => e.StartsWith("quarter"));
        errors.Should().Contain(e => e.Contains("'Adults' is not a STAR risk group"));
        errors.Should().Contain(e => e.StartsWith("contractor"));
        errors.Should().Contain(e => e.StartsWith("serviceArea"));
    }

    [Fact]
    public void The_expected_csv_is_read_as_premium_billing_writes_it()
    {
        var csv = "member_id,month,rate_cell\nM1,2026-09,STAR-BEXAR-TANF-ADULT\n\"M,2\",2026-10,'-ODD\n";

        HttpExpectedRateCellSource.Parse(new StringReader(csv)).Should().Equal(
            new ExpectedRateCell("M1", M(2026, 9), "STAR-BEXAR-TANF-ADULT"),
            new ExpectedRateCell("M,2", M(2026, 10), "-ODD"));
    }

    // ---- The workbook -------------------------------------------------------

    private static readonly DateOnly[] TemplateMonths = [M(2026, 9), M(2026, 10), M(2026, 11)];

    /// <summary>A small workbook laid out like HHSC's: eight sections, months down and across, subtotal formulas, a Total section.</summary>
    internal static byte[] Template(bool dropPregnant = false)
    {
        var rows = new SortedDictionary<int, List<string>>();
        void Text(int row, string col, string text) =>
            (rows.TryGetValue(row, out var l) ? l : rows[row] = []).Add($"<c r=\"{col}{row}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>");
        void Formula(int row, string col, string f) =>
            (rows.TryGetValue(row, out var l) ? l : rows[row] = []).Add($"<c r=\"{col}{row}\" s=\"3\"><f>{f}</f><v>0</v></c>");
        void Blank(int row, string col) =>
            (rows.TryGetValue(row, out var l) ? l : rows[row] = []).Add($"<c r=\"{col}{row}\" s=\"2\"/>");

        Text(1, "A", "STAR Claims Lag Report");
        foreach (var (row, label) in new[] { (2, "Contractor:"), (3, "Service Area:"), (4, "SFY:"), (5, "Quarter:"), (6, "Date Submitted:") })
            Text(row, "A", label);

        var cols = new[] { "B", "C", "D" };
        var r = 7;
        foreach (var (_, title) in TexasStarRiskGroups.All)
        {
            if (dropPregnant && title == "Pregnant Woman") continue;
            Text(r, "A", title);
            Text(r, "B", "Month Paid (Across)");
            Text(r + 1, "A", "Month Incurred (Down)");
            for (var c = 0; c < 3; c++) Text(r + 1, cols[c], $"Month Paid \n{TemplateMonths[c]:MMM yyyy}");
            for (var i = 0; i < 3; i++)
            {
                Text(r + 2 + i, "A", $"Month Incurred {TemplateMonths[i]:MMM yyyy}");
                foreach (var col in cols) Blank(r + 2 + i, col);
                Formula(r + 2 + i, "E", $"SUM(B{r + 2 + i}:D{r + 2 + i})");
            }
            Text(r + 5, "A", title + " Subtotal");
            foreach (var col in cols) Formula(r + 5, col, $"SUM({col}{r + 2}:{col}{r + 4})");
            r += 6;
        }
        Text(r, "A", "Total STAR Claims Lag");

        static int ColumnOf(string cell) => cell[7] - 'A';
        var sheetData = string.Concat(rows.Select(kv => $"<row r=\"{kv.Key}\">{string.Concat(kv.Value.OrderBy(ColumnOf))}</row>"));
        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["xl/workbook.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook {ns}><sheets><sheet name=\"STAR Claims Lag Report\" sheetId=\"3\" r:id=\"rId2\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>",
            ["xl/worksheets/sheet2.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet {ns}><sheetData>{sheetData}</sheetData></worksheet>",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, xml) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(xml));
            }
        return output.ToArray();
    }

    private static TexasClaimsLag SampleLag() => TexasClaimsLagReport.Build(2027, 1, M(2026, 9), LagTriangle.Increments(new[]
    {
        new ClaimPaymentVersion("C1", 1, D(2026, 9), D(2026, 10), 100m, false, "M1"),
        new ClaimPaymentVersion("C1", 2, D(2026, 9), D(2026, 11), 80m, false, "M1"),
        new ClaimPaymentVersion("C2", 1, D(2026, 10), D(2026, 10), 300m, false, "M1"),
    }), Cells, Groups());

    [Fact]
    public void The_layout_is_read_from_the_templates_own_labels()
    {
        var (layout, problems) = TexasClaimsLagReport.ReadLayout(XlsxTemplate.Open(Template()));

        problems.Should().BeEmpty();
        layout!.PaidColumns.Should().Equal(new Dictionary<DateOnly, string> { [M(2026, 9)] = "B", [M(2026, 10)] = "C", [M(2026, 11)] = "D" });
        // TANF Adult is the seventh section: title row 7 + 6 * 6 = 43, data from row 45.
        layout.IncurredRows["TANF-ADULT"][M(2026, 9)].Should().Be(45);
    }

    [Fact]
    public void Filling_writes_the_header_and_the_payments_and_drops_stale_totals()
    {
        var workbook = XlsxTemplate.Open(Template());
        var (layout, _) = TexasClaimsLagReport.ReadLayout(workbook);

        var filled = XlsxTemplate.Open(TexasClaimsLagReport.Fill(workbook, layout!, SampleLag(), "Lone Star Health Plan", "Bexar", M(2026, 12)));

        const string s = TexasClaimsLagReport.Sheet;
        filled.Text(s, "B2").Should().Be("Lone Star Health Plan");
        filled.Text(s, "B3").Should().Be("Bexar");
        filled.Text(s, "B4").Should().Be("2027");
        filled.Text(s, "B5").Should().Be("Q1");
        filled.Text(s, "B6").Should().Be("12/01/2026");
        filled.Text(s, "C45").Should().Be("100");  // TANF Adult, Sep service, Oct paid
        filled.Text(s, "D45").Should().Be("-20");  // the recoupment, in November
        filled.Text(s, "C52").Should().Be("300");  // Pregnant Woman (row 49 + 2 + 1), Oct service, Oct paid
        filled.Formula(s, "B48").Should().Be("SUM(B45:B47)");
        filled.Text(s, "B48").Should().BeNull();
    }

    [Fact]
    public void A_template_missing_a_section_or_a_month_is_refused()
    {
        TexasClaimsLagReport.ReadLayout(XlsxTemplate.Open(Template(dropPregnant: true))).Problems
            .Should().ContainSingle().Which.Should().Contain("Pregnant Woman");

        var (layout, _) = TexasClaimsLagReport.ReadLayout(XlsxTemplate.Open(Template()));
        var late = TexasClaimsLagReport.Build(2027, 2, M(2026, 9), LagTriangle.Increments(new[]
        {
            new ClaimPaymentVersion("C1", 1, D(2026, 9), D(2027, 1), 10m, false, "M1"),
        }), Cells, Groups());
        TexasClaimsLagReport.Missing(layout!, late).Should().Equal("The template has no 'Month Paid Jan 2027' column");
    }

    // ---- The endpoints ------------------------------------------------------

    private static (ClaimsFinanceController, Mock<IExpectedRateCellSource>) Controller(params ClaimPaymentVersion[] versions)
    {
        var repo = new Mock<IClaimRepository>();
        repo.Setup(r => r.GetPaymentVersionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), LineOfBusiness.Medicaid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions);
        var source = new Mock<IExpectedRateCellSource>();
        source.Setup(s => s.GetAsync("t1", "TX", M(2026, 9), M(2026, 11), It.IsAny<CancellationToken>())).ReturnsAsync(Cells);
        var controller = new ClaimsFinanceController(repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, source);
    }

    private static readonly ICurrentActor Actor = Mock.Of<ICurrentActor>(a => a.TenantId == "t1" && a.UserId == "finance-1");

    private static TexasClaimsLagRequest Request() => new()
    {
        StateFiscalYear = 2027, Quarter = 1, RateCellRiskGroups = Mapping,
        Contractor = "Lone Star Health Plan", ServiceArea = "Bexar", SubmissionDate = M(2026, 12),
    };

    [Fact]
    public async Task The_workbook_endpoint_fills_the_template_and_reports_exclusions()
    {
        var (controller, source) = Controller(
            new ClaimPaymentVersion("C1", 1, D(2026, 9), D(2026, 10), 100m, false, "M1"),
            new ClaimPaymentVersion("C4", 1, D(2026, 9), D(2026, 9), 70m, false, "M3"));
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Template();

        var result = await controller.TexasClaimsLagWorkbook(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "template", "5-6-2-11.xlsx"), json, source.Object, Actor, default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("STAR-Claims-Lag-SFY2027-Q1.xlsx");
        XlsxTemplate.Open(file.FileContents).Text(TexasClaimsLagReport.Sheet, "C45").Should().Be("100");
        controller.Response.Headers["X-Report-Excluded"].ToString().Should().Contain("1 claim(s), 70.00");
    }

    [Fact]
    public async Task The_json_endpoint_says_when_the_rate_cells_cannot_be_read()
    {
        var (controller, source) = Controller();
        source.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExpectedRateCellsUnavailableException("premium-billing-service answered 503 for the expected member-months"));

        var result = await controller.TexasStarClaimsLag(Request(), source.Object, Actor, default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task The_workbook_endpoint_refuses_something_that_is_not_the_template()
    {
        var (controller, source) = Controller();
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Template(dropPregnant: true);

        var result = await controller.TexasClaimsLagWorkbook(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "template", "x.xlsx"), json, source.Object, Actor, default);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }
}
