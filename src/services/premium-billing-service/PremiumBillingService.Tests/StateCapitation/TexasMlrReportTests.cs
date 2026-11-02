using CloudHealthOffice.Infrastructure.Reporting;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Mlr;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>
/// HHSC's MLR Report (UMCM 5.3.13.2 v2.4): the line-by-line figures, and the
/// state's own workbook filled in without disturbing its formulas.
/// </summary>
public class TexasMlrReportTests
{
    // The MLR Report sheet's labels as HHSC writes them (double spaces and all).
    private static readonly (string Cell, string Text)[] Labels =
    [
        ("A6", "1. Number of member months:"),
        ("B8", "Medical expenses:  Incurred claims (incl Rx, excl QI), per FSR"),
        ("C10", "Certain withholds from payments made to network providers, if any\n [reference to, and subject to, 42 CFR § 438.8(e)(2)(i)(C)]"),
        ("C11", "Less:  Certain claims that are recoverable for anticipated coordination of benefits, if any"),
        ("C12", "Less:  Certain claims payment recoveries received as a result of subrogation, if any"),
        ("C13", "Certain incentive and bonus payments made, or expected to be made, to network providers"),
        ("C14", "Certain claims payments recovered through fraud reduction efforts, not to exceed"),
        ("C15", "Certain State directed payments made to providers [ref 42 CFR § 438.8(e)(2)(iii)(C)]. "),
        ("C16", "Other CFR-specified additions to incurred claims, if any"),
        ("C17", "Less:  Overpayment recoveries received from network providers, if any"),
        ("C18", "Less:  Prescription drug rebates received or accrued, if any"),
        ("C19", "Less: Other CFR-specified subtractions to incurred claims, if any"),
        ("C20", "Certain net payments or receipts related to State mandated solvency funds, if any"),
        ("C21", "Incurred claims paid by one MCO that are later assumed by another entity, if any"),
        ("B22", "Certain expenditures that improve health care quality (QI)"),
        ("B23", "Certain expenditures on certain \"fraud prevention activities.\"  Any costs in this line"),
        ("B26", "Gross revenues (premiums, etc), per FSR"),
        ("B27", "Less: Taxes, licensing and regulatory fees, per FSR"),
        ("B28", "Less: Experience rebate (return of State's portion of profit share)"),
        ("B29", "Any other CFR-specified adjustment to Premiums"),
        ("B36", "Partial Credibility Adjustment, if any"),
    ];

    private static readonly (string Cell, string Formula)[] Formulas =
    [
        ("G24", "SUM(G8:G10)-SUM(G11:G12)+SUM(G13:G16)-SUM(G17:G19)+SUM(G20:G23)"),
        ("G30", "+G26-G27-G28+G29"),
        ("G32", "+G24"),
        ("G33", "+G30"),
        ("G34", "IF(G33=0,0,+G32/G33)"),
        ("G37", "+G34+G36"),
    ];

    /// <summary>
    /// A workbook shaped like HHSC's: A1 is a shared string, the other labels
    /// inline, G8 an existing styled blank cell, G10 and row 2 absent (so the
    /// filler has to insert them in order).
    /// </summary>
    internal static byte[] Template(string? g30Formula = null, string version = "Version 2.4")
    {
        var cells = new Dictionary<int, List<string>>();
        void Add(string cell, string xml)
        {
            var row = int.Parse(cell.TrimStart('A', 'B', 'C', 'D', 'E', 'F', 'G'));
            (cells.TryGetValue(row, out var list) ? list : cells[row] = []).Add(xml);
        }
        Add("A1", "<c r=\"A1\" t=\"s\"><v>0</v></c>");
        foreach (var (cell, text) in Labels)
            Add(cell, $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>");
        Add("G8", "<c r=\"G8\" s=\"5\"/>");
        foreach (var (cell, formula) in Formulas)
            Add(cell, $"<c r=\"{cell}\" s=\"7\"><f>{SecurityElement.Escape(cell == "G30" && g30Formula != null ? g30Formula : formula)}</f><v>0</v></c>");

        static int Col(string xml) => xml[7] - 'A';
        var sheetData = string.Concat(cells.OrderBy(c => c.Key).Select(c =>
            $"<row r=\"{c.Key}\">{string.Concat(c.Value.OrderBy(Col))}</row>"));

        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["xl/workbook.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook {ns}><sheets><sheet name=\"Document History\" sheetId=\"32\" r:id=\"rId1\"/><sheet name=\"MLR Report\" sheetId=\"28\" r:id=\"rId2\"/><sheet name=\"Attestation\" sheetId=\"33\" r:id=\"rId3\"/></sheets><calcPr calcId=\"191029\"/></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/></Relationships>",
            ["xl/sharedStrings.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst {ns} count=\"1\" uniqueCount=\"1\"><si><r><t>MCO Medical Loss Ratio </t></r><r><t>(MLR) Report</t></r></si></sst>",
            ["xl/worksheets/sheet1.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet {ns}><sheetData><row r=\"3\"><c r=\"D3\" t=\"inlineStr\"><is><t>{version}</t></is></c></row></sheetData></worksheet>",
            ["xl/worksheets/sheet3.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet {ns}><sheetData><row r=\"3\"><c r=\"B3\"><f>+'MLR Report'!D2</f><v>0</v></c></row><row r=\"10\"><c r=\"C10\"><f>+'MLR Report'!D4</f><v>0</v></c></row></sheetData></worksheet>",
            ["xl/worksheets/sheet2.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet {ns}><sheetData>{sheetData}</sheetData><sheetProtection sheet=\"1\"/></worksheet>",
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

    private static TexasMlrRequest Request() => new()
    {
        StateFiscalYear = 2027,
        McoName = "Lone Star Health Plan, Inc.",
        SubmissionDate = new DateOnly(2028, 7, 31),
        MemberMonths = 120_000,
        IncurredClaims = 85_000_000m,
        ProviderWithholds = 500_000m,
        CoordinationOfBenefitsRecoverable = 200_000m,
        SubrogationRecoveries = 100_000m,
        ProviderIncentives = 300_000m,
        StateDirectedPayments = 2_000_000m,
        OverpaymentRecoveries = 400_000m,
        PrescriptionDrugRebates = 1_000_000m,
        SolvencyFundsNet = -50_000m,
        QualityImprovement = 1_200_000m,
        FraudPrevention = 900_000m,
        GrossRevenue = 103_000_000m,
        TaxesAndFees = 2_000_000m,
        ExperienceRebate = 1_000_000m,
        OtherDenominatorAdjustment = 0m,
    };

    [Fact]
    public void The_figures_follow_the_sheets_formulas_and_cap_fraud_prevention()
    {
        var f = TexasMlrReport.Compute(Request(), capitationReceived: 0m, expectedMemberMonths: 0, MlrStandard.Texas);

        // Denominator G26 - G27 - G28 + G29 = 103M - 2M - 1M.
        f.Denominator.Should().Be(100_000_000m);
        // Fraud prevention is counted up to 0.5% of the denominator: 500,000 of 900,000.
        f.Lines.Single(l => l.Line == "2.4").Amount.Should().Be(500_000m);
        // 85M + .5M - .2M - .1M + .3M + 2M - .4M - 1M - .05M + 1.2M + .5M
        f.Numerator.Should().Be(87_750_000m);
        f.Mlr.UnadjustedMlr.Should().Be(0.8775m);
        f.Mlr.Credibility.Should().Be(MlrCredibility.PartiallyCredible);
        f.Lines.Single(l => l.Cell == "G36").Amount.Should().Be(f.Mlr.CredibilityAdjustment).And.BeGreaterThan(0m);
        f.PeriodStart.Should().Be(new DateOnly(2026, 9, 1));
        f.PeriodEnd.Should().Be(new DateOnly(2027, 8, 31));
        f.Mlr.Notes.Should().Contain(n => n.Contains("Fraud prevention capped"));
    }

    [Fact]
    public void Gross_revenue_defaults_to_the_820_capitation_and_says_so()
    {
        var r = Request();
        r.GrossRevenue = null;
        r.OtherPremiumRevenue = 3_000_000m;
        r.MemberMonths = null;

        var f = TexasMlrReport.Compute(r, capitationReceived: 100_000_000m, expectedMemberMonths: 118_500, MlrStandard.Texas);

        f.Lines.Single(l => l.Cell == "G26").Amount.Should().Be(103_000_000m);
        f.MemberMonths.Should().Be(118_500);
        f.Mlr.Notes.Should().Contain(n => n.Contains("per the FSR"));
    }

    [Fact]
    public void Filling_writes_the_header_and_inputs_and_keeps_the_formulas()
    {
        var workbook = XlsxTemplate.Open(Template());
        TexasMlrReport.CheckLayout(workbook).Should().BeEmpty();
        TexasMlrReport.Version(workbook).Should().Be("2.4");
        var f = TexasMlrReport.Compute(Request(), 0m, 0, MlrStandard.Texas);

        var filled = XlsxTemplate.Open(TexasMlrReport.Fill(workbook, f, "Lone Star Health Plan, Inc.", new DateOnly(2028, 7, 31)));

        const string s = TexasMlrReport.Sheet;
        filled.Text(s, "D2").Should().Be("Lone Star Health Plan, Inc.");
        filled.Text(s, "D3").Should().Be("2027");
        filled.Text(s, "D4").Should().Be("46965"); // 2028-07-31 as an Excel serial day
        filled.Text(s, "F5").Should().Be("46630"); // 2027-08-31
        filled.Text(s, "G6").Should().Be("120000");
        filled.Text(s, "G8").Should().Be("85000000");
        filled.Text(s, "G10").Should().Be("500000");
        filled.Text(s, "G20").Should().Be("-50000");
        filled.Text(s, "G23").Should().Be("500000");
        filled.Text(s, "G26").Should().Be("103000000");
        foreach (var (cell, formula) in Formulas)
            filled.Formula(s, cell).Should().Be(formula);
        filled.Text(s, "G24").Should().Be("87750000");
        filled.Text(s, "G34").Should().Be("0.8775");
        // Previewers that don't recalculate still see the attestation header.
        filled.Text("Attestation", "B3").Should().Be("Lone Star Health Plan, Inc.");
        filled.Text("Attestation", "C10").Should().Be("46965");
        filled.Formula("Attestation", "B3").Should().Be("+'MLR Report'!D2");
        // The labels are untouched and the workbook still passes the layout check.
        TexasMlrReport.CheckLayout(filled).Should().BeEmpty();
    }

    [Fact]
    public void Inserted_cells_keep_row_and_column_order_and_existing_styles()
    {
        var workbook = XlsxTemplate.Open(Template());
        var bytes = TexasMlrReport.Fill(workbook, TexasMlrReport.Compute(Request(), 0m, 0, MlrStandard.Texas), "MCO", new DateOnly(2028, 7, 31));

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()).ReadToEnd();
        var workbookXml = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();

        sheet.IndexOf("<row r=\"2\"", StringComparison.Ordinal).Should().BeLessThan(sheet.IndexOf("<row r=\"6\"", StringComparison.Ordinal));
        sheet.IndexOf("r=\"C10\"", StringComparison.Ordinal).Should().BeLessThan(sheet.IndexOf("r=\"G10\"", StringComparison.Ordinal));
        sheet.Should().Contain("<c r=\"G8\" s=\"5\"><v>85000000</v></c>");
        sheet.Should().Contain("<sheetProtection sheet=\"1\" />");
        workbookXml.Should().Contain("fullCalcOnLoad=\"1\"");
        zip.Entries.Select(e => e.FullName).Should().Equal(
            "[Content_Types].xml", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/sharedStrings.xml",
            "xl/worksheets/sheet1.xml", "xl/worksheets/sheet3.xml", "xl/worksheets/sheet2.xml");
    }

    [Fact]
    public void Clearing_cached_values_keeps_formulas_and_drops_their_stale_results()
    {
        var workbook = XlsxTemplate.Open(Template());
        workbook.ClearCachedValues(TexasMlrReport.Sheet);

        var saved = XlsxTemplate.Open(workbook.Save());

        saved.Formula(TexasMlrReport.Sheet, "G24").Should().NotBeNull();
        saved.Text(TexasMlrReport.Sheet, "G24").Should().BeNull();
        saved.Text(TexasMlrReport.Sheet, "B26").Should().StartWith("Gross revenues");
    }

    [Fact]
    public void A_changed_template_is_refused_not_misfilled()
    {
        var workbook = XlsxTemplate.Open(Template(g30Formula: "+G26-G27-G28+G29+G31"));

        TexasMlrReport.CheckLayout(workbook).Should().ContainSingle()
            .Which.Should().Contain("G30");
    }

    [Fact]
    public void Something_that_is_not_a_workbook_is_refused()
    {
        var act = () => XlsxTemplate.Open(Encoding.UTF8.GetBytes("Member,Amount\n1,2\n"));
        act.Should().Throw<XlsxTemplateException>();
    }

    [Fact]
    public void Validation_requires_a_fiscal_year_and_positive_less_lines()
    {
        var r = Request();
        r.StateFiscalYear = 27;
        r.PrescriptionDrugRebates = -5m;
        r.McoName = null;

        var errors = r.Validate(forWorkbook: true);

        errors.Should().Contain(e => e.StartsWith("stateFiscalYear"));
        errors.Should().Contain(e => e.StartsWith("prescriptionDrugRebates"));
        errors.Should().Contain(e => e.StartsWith("mcoName"));
        r.Validate(forWorkbook: false).Should().NotContain(e => e.StartsWith("mcoName"));
    }

    private static StateCapitationController Controller()
    {
        var store = new Mock<IStateCapitationStore>();
        store.Setup(s => s.GetPaymentsAsync(It.IsAny<string>())).ReturnsAsync(Array.Empty<RemittanceAdvice>());
        store.Setup(s => s.GetExpectedAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
            .ReturnsAsync(Array.Empty<ExpectedMemberMonth>());
        var actor = Mock.Of<ICurrentActor>(a => a.UserId == "finance-1" && a.TenantId == "t");
        return new StateCapitationController(store.Object, actor, NullLogger<StateCapitationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static IFormFile File(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "template", "5-3-13-2.xlsx");

    [Fact]
    public async Task The_workbook_endpoint_returns_the_filled_template()
    {
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var result = await Controller().TexasMlrWorkbook("tx", File(Template()), json);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("MLR-Report-SFY2027.xlsx");
        XlsxTemplate.Open(file.FileContents).Text(TexasMlrReport.Sheet, "D2").Should().Be("Lone Star Health Plan, Inc.");
    }

    [Fact]
    public async Task The_workbook_endpoint_refuses_other_states_other_layouts_and_bad_requests()
    {
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        (await Controller().TexasMlrWorkbook("CA", File(Template()), json)).Should().BeOfType<NotFoundObjectResult>();
        (await Controller().TexasMlrWorkbook("TX", File(Template(g30Formula: "+G26")), json)).Should().BeOfType<UnprocessableEntityObjectResult>();
        (await Controller().TexasMlrWorkbook("TX", File(Encoding.UTF8.GetBytes("not a workbook")), json)).Should().BeOfType<UnprocessableEntityObjectResult>();
        (await Controller().TexasMlrWorkbook("TX", null, json)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller().TexasMlrWorkbook("TX", File(Template()), "{")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task The_json_endpoint_returns_the_lines_with_their_cells()
    {
        var result = await Controller().TexasMlr("TX", Request());

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        body.GetProperty("figures").GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("cell").GetString() == "G36").GetProperty("line").GetString().Should().Be("5.1");
    }
}
