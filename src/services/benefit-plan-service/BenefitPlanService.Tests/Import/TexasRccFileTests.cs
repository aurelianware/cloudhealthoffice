using BenefitPlanService.Import.MedicaidDrg;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using FluentAssertions;
using Xunit;

namespace BenefitPlanService.Tests.Import;

/// <summary>HHSC's "Inpatient Ratio of Cost to Charges" PDFs, as text lines of column segments.</summary>
public class TexasRccFileTests
{
    private static (int, IReadOnlyList<string>) Line(params string[] segments) => (1, segments);

    private static readonly (int, IReadOnlyList<string>) Title = Line("Inpatient Ratio of Cost to Charges Effective 10/01/2026");

    // Real rows from the 10/01/2026 file (public HHSC data).
    private static readonly (int, IReadOnlyList<string>) Andrews = Line(
        "127298107", "1174563779", "ANDREWS COUNTY HOSPITAL DISTRICT", "720 HOSPITAL DR", "ANDREWS, TX 79714-3617", "ANDREWS", "08/01/2026", "98%", "Rural");

    // Printed with the effective date as an Excel serial formatted as a percentage.
    private static readonly (int, IReadOnlyList<string>) BaylorAllSaints = Line(
        "135036506", "1669472387", "BAYLOR ALL SAINTS MEDICAL CENTER-BAYLOR SCOTT & WHITE ALL SAINTS MEDICAL CENTER FOR",
        "1400 8TH AVE", "FORT WORTH, TX 76104-4110", "TARRANT", "4626600%", "28%", "Urban");

    private static (int, IReadOnlyList<string>) Site(string tpi, string date, string rate) => Line(
        tpi, "1316936990", "SOME HOSPITAL", "1 MAIN ST", "AUSTIN, TX 78701", "TRAVIS", date, rate, "Urban");

    [Fact]
    public void Reads_rows_and_turns_rates_into_fractions()
    {
        var file = TexasRccFileReader.Parse([Title, Andrews]);

        file.FileEffectiveDate.Should().Be(new DateTime(2026, 10, 1));
        var row = file.Rates.Single();
        row.Tpi.Should().Be("127298107");
        row.Npi.Should().Be("1174563779");
        row.ProviderName.Should().Be("ANDREWS COUNTY HOSPITAL DISTRICT");
        row.County.Should().Be("ANDREWS");
        row.RateEffectiveDate.Should().Be(new DateTime(2026, 8, 1));
        row.Rate.Should().Be(0.98m);
        row.HospitalClass.Should().Be("Rural");
    }

    [Fact]
    public void An_effective_date_printed_as_an_excel_serial_percentage_is_read_as_a_date()
    {
        var file = TexasRccFileReader.Parse([Title, BaylorAllSaints]);

        file.Rates.Single().RateEffectiveDate.Should().Be(new DateTime(2026, 9, 1)); // serial 46266
        file.Rates.Single().County.Should().Be("TARRANT");
        file.SerialDatesRepaired.Should().Be(1);
    }

    [Fact]
    public void An_effective_date_printed_as_a_bare_serial_after_the_rate_is_read_as_a_date()
    {
        // As in the 10/01/2026 file; the 09/01/2026 file prints 06/12/2012 for this TPI.
        var file = TexasRccFileReader.Parse([Title, Line(
            "333289202", "1457791105 DALLAS BEHAVIORAL HEALTHCARE HOSPITAL LLC", "800 KIRNWOOD DR", "DESOTO, TX 75115-2000", "DALLAS", "50%", "41072", "PSYCHIATRIC")]);

        var row = file.Rates.Single();
        row.RateEffectiveDate.Should().Be(new DateTime(2012, 6, 12));
        row.Rate.Should().Be(0.50m);
        row.HospitalClass.Should().Be("PSYCHIATRIC");
        row.ProviderName.Should().Be("DALLAS BEHAVIORAL HEALTHCARE HOSPITAL LLC");
        file.SerialDatesRepaired.Should().Be(1);
    }

    [Fact]
    public void Sites_of_one_npi_with_the_same_rate_collapse_to_one()
    {
        var file = TexasRccFileReader.Parse([Title, Site("100000001", "07/01/2026", "34%"), Site("100000002", "07/01/2026", "34%")]);

        file.Rows.Should().HaveCount(2);
        file.Rates.Should().ContainSingle();
        file.HeldBack.Should().BeEmpty();
    }

    [Fact]
    public void An_npi_whose_sites_carry_different_rates_is_held_back()
    {
        var file = TexasRccFileReader.Parse([Title, Andrews, Site("100000001", "07/01/2026", "34%"), Site("100000002", "07/24/2026", "50%")]);

        file.Rates.Should().ContainSingle().Which.Npi.Should().Be("1174563779");
        file.HeldBack.Should().ContainSingle().Which.Problem.Should().Contain("different rates");
    }

    [Fact]
    public void An_alphanumeric_tpi_is_read()
    {
        var file = TexasRccFileReader.Parse([Title, Line(
            "1333551A4", "1205900370", "HARRIS COUNTY HOSPITAL DISTRICT", "5656 KELLEY ST", "HOUSTON, TX 77026-1967", "HARRIS", "07/01/2026", "92%", "Urban")]);

        file.Rates.Single().Tpi.Should().Be("1333551A4");
    }

    [Fact]
    public void A_row_that_does_not_read_refuses_the_file()
    {
        var act = () => TexasRccFileReader.Parse([Title, Line("127298107", "1174563779", "ANDREWS", "ANDREWS", "98%", "Rural")]);

        act.Should().Throw<InterimRateFormatException>().WithMessage("*could not be read*");
    }

    [Fact]
    public void A_file_without_the_title_is_refused()
    {
        var act = () => TexasRccFileReader.Parse([Andrews]);

        act.Should().Throw<InterimRateFormatException>().WithMessage("*not a Texas RCC file*");
    }

    /// <summary>
    /// Imports the real HHSC RCC PDFs when CHO_TX_RCC_DIR points at a folder
    /// holding them (public, not committed). Skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Imports_the_published_files_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_RCC_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var store = new InMemoryMedicaidInterimRateRepository();
        var service = new TexasInterimRateImportService(store);
        foreach (var path in Directory.GetFiles(dir, "*rcc*.pdf").Order())
        {
            await using var stream = File.OpenRead(path);
            var report = await service.ImportAsync(stream, Path.GetFileName(path), new InterimRateImportOptions { DryRun = false }, "t", "test");
            report.Rows.Should().NotBeEmpty(path);
            Console.WriteLine($"{Path.GetFileName(path)}: {report.Columns["format"]}, {report.Columns["rows"]} rows, {report.Rows.Count} rates, " +
                string.Join(", ", report.Rows.GroupBy(r => r.Outcome).Select(g => $"{g.Key} {g.Count()}")) + $", held back {report.HeldBack.Count}");
            foreach (var w in report.Warnings) Console.WriteLine($"  warning: {w}");
            foreach (var h in report.HeldBack) Console.WriteLine($"  held: {h.Npi} {h.ProviderName}: {h.Problem}");
        }
    }
}
