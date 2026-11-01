using BenefitPlanService.Import.MedicaidDrg;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace BenefitPlanService.Tests.Import;

/// <summary>Texas APR-DRG SDA PDFs, laid out like HHSC's SFY 2027 urban and rural files.</summary>
public class TexasSdaFileTests
{
    internal static readonly string[] UrbanTitle =
    [
        "State Fiscal Year 2027",
        "All Patient Refined Diagnosis Related Group (APR DRG)",
        "Standard Dollar Amounts for Urban Hospitals",
        "Universal Mean Effective September 1, 2026 - $10,161.08",
    ];

    internal static readonly string[] RuralTitle =
    [
        "State Fiscal Year 2027",
        "All Patient Refined Diagnosis Related Group (APR DRG)",
        "Standard Dollar Amounts for Rural Hospitals",
        "Universal Mean Effective September 1, 2026 - $10,161.08",
    ];

    // Real SFY 2027 rows (public HHSC data).
    internal static readonly string[] Waco =
    [
        "111829108", "1093708679", "450042", "ASCENSION PROVIDENCE", "6901 MEDICAL PARKWAY", "WACO", "76712", "MCLENNAN",
        "$2,993.72", "$139.21", "$150.16", "$49.49", "$59.54", "$320.75", "$3,712.87",
    ];

    internal static readonly string[] Houston =
    [
        "365048301", "1669732178", "670102", "AD HOSPITAL EAST LLC", "12950 EAST FWY", "HOUSTON", "77015", "HARRIS",
        "$2,993.72", "$139.21", "$432.10", "$0.00", "$0.00", "$0.00", "$3,565.03",
    ];

    internal static readonly string[] Andrews =
    [
        "127298107", "1174563779", "450144", "ANDREWS COUNTY HOSPITAL DISTRICT", "720 HOSPITAL DR", "ANDREWS", "79714", "ANDREWS",
        "$17,296.96", "$576.53", "$17,873.49", "$23,188.77",
    ];

    [Fact]
    public void Reads_an_urban_file_with_every_add_on()
    {
        var file = TexasSdaFileReader.Read(Pdf(UrbanTitle, Waco, Houston));

        file.HospitalClass.Should().Be(TexasHospitalClass.Urban);
        file.StateFiscalYear.Should().Be(2027);
        file.UniversalMean.Should().Be(10161.08m);
        file.EffectiveDate.Should().Be(new DateTime(2026, 9, 1));
        file.Rows.Should().HaveCount(2);

        var waco = file.Rows[0];
        waco.Tpi.Should().Be("111829108");
        waco.Npi.Should().Be("1093708679");
        waco.MedicareNumber.Should().Be("450042");
        waco.HospitalName.Should().Be("ASCENSION PROVIDENCE");
        waco.City.Should().Be("WACO");
        waco.Zip.Should().Be("76712");
        waco.County.Should().Be("MCLENNAN");
        waco.BaseSda.Should().Be(2993.72m);
        waco.AddOns["IME Add-on"].Should().Be(49.49m);
        waco.AddOns["Safety Net Add-on"].Should().Be(320.75m);
        waco.FinalSda.Should().Be(3712.87m);
        waco.DeliverySda.Should().BeNull();

        var hospital = file.ToPricerHospital(waco, interimRate: 0.35m);
        hospital.BaseRate.Should().Be(3712.87m);
        hospital.CostToChargeRatio.Should().Be(0.35m);
        hospital.TexasClass.Should().Be(TexasHospitalClass.Urban);
    }

    [Fact]
    public void Reads_a_rural_file_with_the_delivery_sda()
    {
        var file = TexasSdaFileReader.Read(Pdf(RuralTitle, Andrews));

        file.HospitalClass.Should().Be(TexasHospitalClass.Rural);
        var row = file.Rows.Single();
        row.BaseSda.Should().Be(17296.96m);
        row.AddOns["Rural Add-on"].Should().Be(576.53m);
        row.FinalSda.Should().Be(17873.49m);
        row.DeliverySda.Should().Be(23188.77m);
        file.ToPricerHospital(row).DeliveryBaseRate.Should().Be(23188.77m);
    }

    [Fact]
    public void Holds_back_a_row_whose_columns_do_not_add_up_and_loads_the_rest()
    {
        // HHSC's SFY 2027 rural file prints Memorial Hospital of Polk County
        // with a $576.53 add-on but an SDA-with-add-on equal to the SDA alone.
        var bad = (string[])Waco.Clone();
        bad[^1] = "$3,999.99";

        var file = TexasSdaFileReader.Read(Pdf(UrbanTitle, bad, Houston));

        file.Rows.Should().ContainSingle().Which.Tpi.Should().Be("365048301");
        var held = file.Discrepancies.Should().ContainSingle().Subject;
        held.Tpi.Should().Be("111829108");
        held.HospitalName.Should().Be("ASCENSION PROVIDENCE");
        held.Problem.Should().Contain("3712.87").And.Contain("3999.99");
    }

    [Fact]
    public void Refuses_a_file_where_no_row_adds_up()
    {
        var bad = (string[])Waco.Clone();
        bad[^1] = "$3,999.99";

        var act = () => TexasSdaFileReader.Read(Pdf(UrbanTitle, bad));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*No row's columns add up*");
    }

    [Fact]
    public void Refuses_a_row_with_the_wrong_number_of_dollar_columns()
    {
        var act = () => TexasSdaFileReader.Read(Pdf(RuralTitle, Waco));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*7 dollar columns where the Rural layout has 4*");
    }

    [Fact]
    public void Refuses_a_file_without_the_title_or_universal_mean()
    {
        var noTitle = () => TexasSdaFileReader.Read(Pdf(["Some other report"], Waco));
        noTitle.Should().Throw<TexasSdaFormatException>().WithMessage("*title naming the hospital class*");

        var noMean = () => TexasSdaFileReader.Read(Pdf(UrbanTitle[..3], Waco));
        noMean.Should().Throw<TexasSdaFormatException>().WithMessage("*Universal Mean*");
    }

    [Fact]
    public void Refuses_a_childrens_file_until_its_layout_is_known()
    {
        var act = () => TexasSdaFileReader.Read(Pdf(["Standard Dollar Amounts for Children's Hospitals"], Waco));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*Children's*not supported*");
    }

    [Fact]
    public void Refuses_a_non_pdf()
    {
        var act = () => TexasSdaFileReader.Read(new MemoryStream("TPI,NPI\n"u8.ToArray()));

        act.Should().Throw<TexasSdaFormatException>().WithMessage("*PDF*");
    }

    /// <summary>
    /// Reads the real HHSC files when CHO_TX_SDA_DIR points at a folder
    /// holding them (they are public but not committed). Skipped otherwise.
    /// </summary>
    [Fact]
    public void Reads_the_published_files_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_SDA_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var path in Directory.GetFiles(dir, "*sda*.pdf"))
        {
            using var stream = File.OpenRead(path);
            var file = TexasSdaFileReader.Read(stream);
            file.Rows.Should().NotBeEmpty(path);
            file.Rows.Should().OnlyContain(r => r.FinalSda > 0 && r.Npi.Length == 10);
            Console.WriteLine($"{Path.GetFileName(path)}: {file.HospitalClass}, SFY {file.StateFiscalYear}, UM {file.UniversalMean}, " +
                $"{file.Rows.Count} rows, {file.Discrepancies.Count} held back, duplicate NPIs: {string.Join(",", file.DuplicateNpis)}");
            foreach (var r in file.Rows.Where(r => r.City is null)) Console.WriteLine($"  nocity: {r.HospitalName}");
            foreach (var d in file.Discrepancies) Console.WriteLine($"  held: {d.Tpi} {d.HospitalName}: {d.Problem}");
        }
    }

    /// <summary>A landscape page: title lines, then each row's columns at their own x like the Excel export.</summary>
    internal static MemoryStream Pdf(IEnumerable<string> title, params string[][] rows)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(1008, 612);
        var y = 590.0;
        foreach (var line in title)
        {
            page.AddText(line, 8, new PdfPoint(300, y), font);
            y -= 12;
        }

        // TPI, NPI, Medicare, name, street, city, ZIP, county, then amounts.
        double[] x = [10, 60, 115, 160, 420, 520, 600, 640];
        foreach (var row in rows)
        {
            y -= 10;
            for (var i = 0; i < row.Length; i++)
            {
                var at = i < x.Length ? x[i] : 700 + (i - x.Length) * 45;
                page.AddText(row[i], 6, new PdfPoint(at, y), font);
            }
        }
        return new MemoryStream(builder.Build());
    }
}
