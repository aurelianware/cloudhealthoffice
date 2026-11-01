using BenefitPlanService.Import.MedicaidDrg;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace BenefitPlanService.Tests.Import;

/// <summary>The Texas APR-DRG weight table PDF, laid out like HHSC's SFY 2027 file.</summary>
public class TexasDrgWeightFileTests
{
    private static readonly string[] Title =
    [
        "State Fiscal Year 2027",
        "All Patient Refined Diagnosis Related Group (APR DRG)",
        "Grouper Version 40",
        "Effective for Discharges on or After 09/01/2026",
        "Universal Mean=10,161.08",
    ];

    /// <summary>A row: DRG, SOI, key, description lines, weight, MLOS, threshold.</summary>
    private sealed record Row(string Drg, string Soi, string[] Description, string Weight, string Mlos, string Day);

    private static Row R(string drg, string soi, string weight, string mlos, string day, params string[] desc) =>
        new(drg, soi, desc.Length == 0 ? ["LIVER TRANSPLANT AND/OR INTESTINAL TRANSPLANT"] : desc, weight, mlos, day);

    private static IEnumerable<Row> FourSeverities(string drg, params string[] desc) =>
    [
        R(drg, "1", "8.4410", "9.3", "19", desc),
        R(drg, "2", "8.7703", "10.9", "24", desc),
        R(drg, "3", "11.4183", "12.6", "28", desc),
        R(drg, "4", "22.1618", "32.7", "79", desc),
    ];

    /// <summary>A weight table PDF with DRG 001's four severities and the given title.</summary>
    internal static MemoryStream WeightPdf(string[]? title = null, string weight1 = "8.4410")
    {
        var rows = FourSeverities("001").ToList();
        rows[0] = rows[0] with { Weight = weight1 };
        return Pdf(rows, title);
    }

    internal static string[] TitleFor(string effective, string mean) =>
        ["State Fiscal Year 2027", "Grouper Version 40", $"Effective for Discharges on or After {effective}", $"Universal Mean={mean}"];

    [Fact]
    public void Reads_the_title_and_every_severity()
    {
        var file = TexasDrgWeightFileReader.Read(Pdf(FourSeverities("001")));

        file.StateFiscalYear.Should().Be(2027);
        file.GrouperVersion.Should().Be("40");
        file.EffectiveDate.Should().Be(new DateTime(2026, 9, 1));
        file.UniversalMean.Should().Be(10161.08m);
        file.Rows.Should().HaveCount(4);
        var row = file.Rows.Single(r => r.DrgSoi == "001-3");
        row.RelativeWeight.Should().Be(11.4183m);
        row.MeanLengthOfStay.Should().Be(12.6m);
        row.DayOutlierThreshold.Should().Be(28m);
        file.Descriptions["001"].Should().Be("LIVER TRANSPLANT AND/OR INTESTINAL TRANSPLANT");
    }

    [Fact]
    public void Pairs_numbers_that_sit_between_the_lines_of_a_wrapped_description()
    {
        // Excel centres the numbers on a row whose description wraps, so they
        // land on neither the key's baseline nor the next row's.
        var wrapped = FourSeverities("588", "NEONATE BIRTH WEIGHT < 1500 GRAMS", "WITH MAJOR PROCEDURE").ToList();

        var file = TexasDrgWeightFileReader.Read(Pdf(wrapped.Concat(FourSeverities("001"))));

        file.Rows.Should().HaveCount(8);
        file.Rows.Single(r => r.DrgSoi == "588-4").RelativeWeight.Should().Be(22.1618m);
        file.Rows.Single(r => r.DrgSoi == "001-1").RelativeWeight.Should().Be(8.4410m);
    }

    [Fact]
    public void Skips_the_ungroupable_drgs()
    {
        var rows = FourSeverities("001").Append(R("956", "0", "-", "-", "-", "UNGROUPABLE"));

        var file = TexasDrgWeightFileReader.Read(Pdf(rows));

        file.Rows.Should().HaveCount(4);
        file.Unpriced.Should().Equal("956-0");
    }

    [Fact]
    public void Refuses_a_drg_without_all_four_severities()
    {
        var act = () => TexasDrgWeightFileReader.Read(Pdf(FourSeverities("001").Take(3)));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*001 has 3 severity rows*");
    }

    [Fact]
    public void Refuses_a_missing_value()
    {
        var rows = FourSeverities("001").ToList();
        rows[2] = rows[2] with { Mlos = "" };

        var act = () => TexasDrgWeightFileReader.Read(Pdf(rows));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*4 DRG-SOI rows but 3 values in the MLOS column*");
    }

    [Fact]
    public void Refuses_a_file_without_the_title()
    {
        var act = () => TexasDrgWeightFileReader.Read(Pdf(FourSeverities("001"), title: ["Something else"]));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*grouper version and the effective*");
    }

    /// <summary>
    /// Reads the real HHSC file when CHO_TX_SDA_DIR points at a folder holding
    /// it (public but not committed). Skipped otherwise.
    /// </summary>
    [Fact]
    public void Reads_the_published_file_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_SDA_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var path in Directory.GetFiles(dir, "*apr-drg*.pdf"))
        {
            using var stream = File.OpenRead(path);
            var file = TexasDrgWeightFileReader.Read(stream);
            Console.WriteLine($"{Path.GetFileName(path)}: SFY {file.StateFiscalYear}, V{file.GrouperVersion}, effective {file.EffectiveDate:yyyy-MM-dd}, " +
                $"UM {file.UniversalMean}, {file.Rows.Count} rows, {file.Descriptions.Count} DRGs, unpriced {string.Join(",", file.Unpriced)}");
            foreach (var key in new[] { "001-1", "139-2", "560-1", "588-4", "640-1" })
            {
                var r = file.Rows.FirstOrDefault(x => x.DrgSoi == key);
                Console.WriteLine($"  {key}: {r?.RelativeWeight} {r?.MeanLengthOfStay} {r?.DayOutlierThreshold} {file.Descriptions.GetValueOrDefault(key[..3])}");
            }
            file.Rows.Should().NotBeEmpty();
        }
    }

    private static readonly string[] SheetTitle =
        ["State Fiscal Year 2027\nAll Patient Refined Diagnosis Related Group (APR DRG)\nGrouper Version 40\nEffective for Discharges on or After 09/01/2026\nUniversal Mean=10,161.08"];

    private static readonly string[] SheetHeader =
        ["APR DRG", "SOI ", "DRG-SOI", "DRG Description", "V40 APR - DRG Relative Weight", "Mean Length of Stay (MLOS)", "Day Outlier Threshold"];

    private static string[] SheetRow(string drg, string soi, string w, string m, string d) =>
        [drg, soi, drg + soi, "LIVER TRANSPLANT AND/OR INTESTINAL TRANSPLANT", w, m, d];

    [Fact]
    public void Reads_the_excel_layout()
    {
        var sheet = new List<string[]>
        {
            SheetTitle, SheetHeader,
            SheetRow("001", "1", "8.441", "9.3", "19"),
            SheetRow("001", "2", "8.7703", "10.9", "24"),
            SheetRow("001", "3", "11.4183", "12.6", "28"),
            SheetRow("001", "4", "22.1618", "32.7", "79"),
            SheetRow("956", "0", "", "", ""),
        };

        var file = TexasDrgWeightFileReader.ParseSheet(sheet);

        file.GrouperVersion.Should().Be("40");
        file.EffectiveDate.Should().Be(new DateTime(2026, 9, 1));
        file.Rows.Should().HaveCount(4);
        file.Rows.Single(r => r.DrgSoi == "001-1").RelativeWeight.Should().Be(8.441m);
        file.Unpriced.Should().Equal("956-0");
    }

    [Fact]
    public void Refuses_an_excel_row_whose_key_disagrees_with_its_drg()
    {
        var sheet = new List<string[]> { SheetTitle, SheetHeader, new[] { "002", "1", "0011", "X", "1.0", "2.0", "3" } };

        var act = () => TexasDrgWeightFileReader.ParseSheet(sheet);

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*do not match the key 0011*");
    }

    /// <summary>
    /// Reads every HHSC weight workbook in CHO_TX_SDA_DIR/weights (public,
    /// not committed). Skipped otherwise.
    /// </summary>
    [Fact]
    public void Reads_the_published_workbooks_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_SDA_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "weights"))) return;

        foreach (var path in Directory.GetFiles(Path.Combine(dir, "weights")).Where(p => p.EndsWith(".xls") || p.EndsWith(".xlsx")).Order())
        {
            using var stream = File.OpenRead(path);
            var file = TexasDrgWeightFileReader.ReadExcel(stream, path);
            var r = file.Rows.First(x => x.DrgSoi == "139-2");
            Console.WriteLine($"{Path.GetFileName(path)}: SFY {file.StateFiscalYear}, V{file.GrouperVersion}, effective {file.EffectiveDate:yyyy-MM-dd}, " +
                $"UM {file.UniversalMean}, {file.Rows.Count} rows; 139-2 = {r.RelativeWeight}/{r.MeanLengthOfStay}/{r.DayOutlierThreshold}");
        }
    }

    /// <summary>A landscape page with the title, header and rows at Excel-like positions.</summary>
    private static MemoryStream Pdf(IEnumerable<Row> rows, string[]? title = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        var y = 770.0;
        foreach (var line in title ?? Title)
        {
            page.AddText(line, 8, new PdfPoint(200, y), font);
            y -= 11;
        }

        // Header: the numeric columns are found by these words.
        y -= 6;
        page.AddText("APR", 6, new PdfPoint(20, y), font);
        page.AddText("DRG-", 6, new PdfPoint(70, y), font);
        page.AddText("Relative", 6, new PdfPoint(420, y), font);
        page.AddText("(MLOS)", 6, new PdfPoint(480, y), font);
        page.AddText("Threshold", 6, new PdfPoint(530, y), font);
        y -= 14;

        foreach (var row in rows)
        {
            // Numbers centred on the row's description block.
            var lines = row.Description.Length;
            var numbersY = y - (lines - 1) * 7.0 / 2;
            page.AddText(row.Drg, 6, new PdfPoint(20, y), font);
            page.AddText(row.Soi, 6, new PdfPoint(50, y), font);
            page.AddText(row.Drg + row.Soi, 6, new PdfPoint(70, y), font);
            for (var i = 0; i < lines; i++)
                page.AddText(row.Description[i], 6, new PdfPoint(110, y - i * 7.0), font);
            if (row.Weight.Length > 0) page.AddText(row.Weight, 6, new PdfPoint(425, numbersY), font);
            if (row.Mlos.Length > 0) page.AddText(row.Mlos, 6, new PdfPoint(485, numbersY), font);
            if (row.Day.Length > 0) page.AddText(row.Day, 6, new PdfPoint(540, numbersY), font);
            y -= Math.Max(10, lines * 7.0 + 3);
        }
        return new MemoryStream(builder.Build());
    }
}
