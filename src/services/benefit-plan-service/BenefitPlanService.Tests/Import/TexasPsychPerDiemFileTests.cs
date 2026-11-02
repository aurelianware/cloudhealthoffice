using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Import.MedicaidDrg;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using FluentAssertions;
using Xunit;

namespace BenefitPlanService.Tests.Import;

/// <summary>HHSC's "SFY yyyy Psychiatric Hospital Rates" PDFs, as text lines of column segments.</summary>
public class TexasPsychPerDiemFileTests
{
    private static (int, IReadOnlyList<string>) Line(params string[] segments) => (1, segments);

    private static readonly (int, IReadOnlyList<string>) Title = Line("SFY 2027 Psychiatric Hospital Rates");
    private static readonly (int, IReadOnlyList<string>) Private = Line("Private Freestanding Psychiatric Facilities (Effective on or before 09/01/2026)");
    private static readonly (int, IReadOnlyList<string>) StateOwned = Line("State-Owned Psychiatric Facilities (Effective 09/01/2026)");

    // Real rows from the SFY 2027 file (public HHSC data).
    private static readonly (int, IReadOnlyList<string>) CypressCreek = Line(
        "021203701", "1730187568 CYPRESS CREEK HOSPITAL INC", "17750 CALI DR", "HOUSTON", "77090", "HARRIS", "$", "563.36");
    private static readonly (int, IReadOnlyList<string>) StateHospital = Line(
        "021194801", "1326052226 HEALTH AND HUMAN SERVICES COMMISSION", "AUSTIN", "78751", "TRAVIS", "$", "567.28");

    [Fact]
    public void Reads_each_section_with_its_effective_date()
    {
        var file = TexasPsychPerDiemFileReader.Parse([Title, Private, CypressCreek, StateOwned, StateHospital]);

        file.FiscalYear.Should().Be(2027);
        file.Rates.Should().HaveCount(2);
        var cypress = file.Rates[0];
        cypress.Tpi.Should().Be("021203701");
        cypress.Npi.Should().Be("1730187568");
        cypress.ProviderName.Should().Be("CYPRESS CREEK HOSPITAL INC");
        cypress.DailyRate.Should().Be(563.36m);
        cypress.Section.Should().Be("Private Freestanding");
        cypress.EffectiveDate.Should().Be(new DateTime(2026, 9, 1));
        file.Rates[1].Section.Should().Be("State-Owned");
    }

    [Fact]
    public void A_per_diem_whose_digits_are_split_is_read()
    {
        var file = TexasPsychPerDiemFileReader.Parse([Title, Private, Line(
            "021203701", "1730187568 CYPRESS CREEK HOSPITAL INC", "HARRIS", "$", "5 6 3 . 3 6")]);

        file.Rates.Single().DailyRate.Should().Be(563.36m);
    }

    [Fact]
    public void An_npi_whose_tpis_carry_different_rates_is_held_back()
    {
        var file = TexasPsychPerDiemFileReader.Parse([Title, Private, CypressCreek,
            Line("021203702", "1730187568 CYPRESS CREEK HOSPITAL INC", "HARRIS", "$", "500.00")]);

        file.Rates.Should().BeEmpty();
        file.HeldBack.Should().ContainSingle().Which.Problem.Should().Contain("different per diems");
    }

    [Fact]
    public void A_row_before_any_section_refuses_the_file()
    {
        var act = () => TexasPsychPerDiemFileReader.Parse([Title, CypressCreek]);

        act.Should().Throw<InterimRateFormatException>().WithMessage("*before any*section*");
    }

    [Fact]
    public void A_row_without_a_per_diem_refuses_the_file()
    {
        var act = () => TexasPsychPerDiemFileReader.Parse([Title, Private, Line("021203701", "1730187568 CYPRESS CREEK HOSPITAL INC", "HARRIS")]);

        act.Should().Throw<InterimRateFormatException>().WithMessage("*no per diem*");
    }

    [Fact]
    public void A_file_without_the_title_is_refused()
    {
        var act = () => TexasPsychPerDiemFileReader.Parse([Private, CypressCreek]);

        act.Should().Throw<InterimRateFormatException>().WithMessage("*not a Texas psychiatric per diem file*");
    }

    [Fact]
    public async Task A_non_pdf_upload_is_refused()
    {
        var service = new TexasPsychPerDiemImportService(new InMemoryMedicaidPsychiatricPerDiemRepository());

        var act = () => service.ImportAsync(new MemoryStream("npi,rate\n"u8.ToArray()), "x.csv", new PsychPerDiemImportOptions(), "t", "test");

        await act.Should().ThrowAsync<InterimRateFormatException>().WithMessage("*psychiatric hospital per diem PDF*");
    }

    /// <summary>
    /// Imports the real HHSC psychiatric per diem PDFs when CHO_TX_PSYCH_DIR
    /// points at a folder holding them (public, not committed). Skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Imports_the_published_files_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_PSYCH_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var store = new InMemoryMedicaidPsychiatricPerDiemRepository();
        var service = new TexasPsychPerDiemImportService(store);
        foreach (var path in Directory.GetFiles(dir, "*psychiatric*.pdf").Order())
        {
            await using var stream = File.OpenRead(path);
            var report = await service.ImportAsync(stream, Path.GetFileName(path), new PsychPerDiemImportOptions { DryRun = false }, "t", "test");
            report.Rows.Should().NotBeEmpty(path);
            report.Rows.Should().OnlyContain(r => r.Outcome == FeeScheduleImportOutcome.Created);
            Console.WriteLine($"{Path.GetFileName(path)}: SFY {report.FiscalYear}, {report.FileRows} rows, {report.Rows.Count} rates (" +
                string.Join(", ", report.Rows.GroupBy(r => (r.FacilityClass, r.EffectiveDate)).Select(g => $"{g.Key.FacilityClass} {g.Key.EffectiveDate:yyyy-MM-dd}: {g.Count()}")) +
                $"), held back {report.HeldBack.Count}");
            foreach (var h in report.HeldBack) Console.WriteLine($"  held: {h.Npi} {h.ProviderName}: {h.Problem}");
        }
    }
}
