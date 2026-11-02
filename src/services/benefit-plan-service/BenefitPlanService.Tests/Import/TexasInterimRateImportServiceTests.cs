using System.Text;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Import.MedicaidDrg;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using FluentAssertions;
using Xunit;

namespace BenefitPlanService.Tests.Import;

public class TexasInterimRateImportServiceTests
{
    private const string Tenant = "tenant-a";
    private readonly InMemoryMedicaidInterimRateRepository _store = new();
    private TexasInterimRateImportService Service => new(_store);

    private static readonly InterimRateImportOptions Write = new() { DryRun = false };

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private const string Sheet =
        "Texas Provider Identifier,NPI,Hospital Name,Inpatient Interim Rate,Outpatient Interim Rate,Effective Date\n" +
        "100000001,1234567893,Waco General,0.4520,0.3810,09/01/2026\n" +
        "100000002,1111111112,Andrews County,51.5%,,09/01/2026\n";

    [Fact]
    public async Task A_dry_run_reports_each_hospital_and_writes_nothing()
    {
        var report = await Service.ImportAsync(Csv(Sheet), "interim.csv", new InterimRateImportOptions(), Tenant, "admin");

        report.DryRun.Should().BeTrue();
        report.Rows.Should().HaveCount(2);
        report.Rows.Should().OnlyContain(r => r.Outcome == FeeScheduleImportOutcome.WouldWrite);
        report.Rows[0].InpatientRate.Should().Be(0.4520m);
        report.Rows[0].OutpatientRate.Should().Be(0.3810m);
        report.Rows[1].InpatientRate.Should().Be(0.515m); // percentage read as a fraction
        report.Rows[1].OutpatientRate.Should().BeNull();
        report.Columns["inpatientRate"].Should().Be("Inpatient Interim Rate");
        _store.Rates.Should().BeEmpty();
    }

    [Fact]
    public async Task Written_rates_are_what_pricing_finds_on_the_discharge_date()
    {
        await Service.ImportAsync(Csv(Sheet), "interim.csv", Write, Tenant, "admin");

        var found = await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Inpatient, new DateTime(2026, 10, 5));
        found!.InpatientRate.Should().Be(0.4520m);
        found.StateProviderId.Should().Be("100000001");
        found.SourceFile.Should().Be("interim.csv");

        (await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Inpatient, new DateTime(2026, 8, 31))).Should().BeNull();
        (await _store.FindAsync(Tenant, "TX", "1111111112", InterimRateKind.Outpatient, new DateTime(2026, 10, 5))).Should().BeNull();
    }

    [Fact]
    public async Task A_letter_that_changes_only_outpatient_leaves_the_inpatient_rate_in_force()
    {
        await Service.ImportAsync(Csv(Sheet), "interim.csv", Write, Tenant, "admin");
        await Service.ImportAsync(Csv("NPI,Outpatient Rate,Effective Date\n1234567893,0.40,01/01/2027\n"), "op.csv", Write, Tenant, "admin");

        var inpatient = await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Inpatient, new DateTime(2027, 2, 1));
        var outpatient = await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Outpatient, new DateTime(2027, 2, 1));
        inpatient!.InpatientRate.Should().Be(0.4520m);
        outpatient!.OutpatientRate.Should().Be(0.40m);
    }

    [Fact]
    public async Task The_request_can_give_the_effective_date_for_the_whole_file()
    {
        var report = await Service.ImportAsync(
            Csv("NPI,Inpatient\n1234567893,0.45\n"), "x.csv",
            new InterimRateImportOptions { EffectiveDate = new DateTime(2026, 9, 1) }, Tenant, "admin");

        report.Rows.Single().EffectiveDate.Should().Be(new DateTime(2026, 9, 1));
    }

    [Fact]
    public async Task Reloading_the_same_file_is_unchanged_and_a_different_rate_needs_replace()
    {
        await Service.ImportAsync(Csv(Sheet), "interim.csv", Write, Tenant, "admin");

        var again = await Service.ImportAsync(Csv(Sheet), "interim.csv", Write, Tenant, "admin");
        again.Rows.Should().OnlyContain(r => r.Outcome == FeeScheduleImportOutcome.Unchanged);

        var changed = Sheet.Replace("0.4520", "0.4600");
        var refused = await Service.ImportAsync(Csv(changed), "interim.csv", Write, Tenant, "admin");
        refused.Rows[0].Outcome.Should().Be(FeeScheduleImportOutcome.Exists);
        (await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Inpatient, new DateTime(2026, 10, 1)))!.InpatientRate.Should().Be(0.4520m);

        var replaced = await Service.ImportAsync(Csv(changed), "interim.csv", Write with { Replace = true }, Tenant, "admin");
        replaced.Rows[0].Outcome.Should().Be(FeeScheduleImportOutcome.Replaced);
        (await _store.FindAsync(Tenant, "TX", "1234567893", InterimRateKind.Inpatient, new DateTime(2026, 10, 1)))!.InpatientRate.Should().Be(0.4600m);
    }

    [Theory]
    [InlineData("1234567890,0.45,09/01/2026", "not a valid NPI")]
    [InlineData("1234567893,45.2,09/01/2026", "write a percentage with a % sign")]
    [InlineData("1234567893,0,09/01/2026", "must be above 0%")]
    [InlineData("1234567893,abc,09/01/2026", "is not a number")]
    [InlineData("1234567893,0.45,13/45/2026", "is not a date")]
    [InlineData("1234567893,,09/01/2026", "has no inpatient or outpatient rate")]
    public async Task One_bad_row_refuses_the_whole_file(string badRow, string message)
    {
        var text = "NPI,Inpatient Interim Rate,Effective Date\n1111111112,0.50,09/01/2026\n" + badRow + "\n";

        var act = () => Service.ImportAsync(Csv(text), "x.csv", Write, Tenant, "admin");

        (await act.Should().ThrowAsync<InterimRateFormatException>()).Which.Message.Should().Contain(message);
        _store.Rates.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_rows_for_one_hospital_and_date_that_disagree_refuse_the_file()
    {
        var text = "NPI,Inpatient,Effective Date\n1234567893,0.45,09/01/2026\n1234567893,0.47,09/01/2026\n";

        var act = () => Service.ImportAsync(Csv(text), "x.csv", Write, Tenant, "admin");

        (await act.Should().ThrowAsync<InterimRateFormatException>()).Which.Message.Should().Contain("different rates");
    }

    [Fact]
    public async Task A_file_without_dates_or_rates_is_refused()
    {
        var noDate = () => Service.ImportAsync(Csv("NPI,Inpatient\n1234567893,0.45\n"), "x.csv", Write, Tenant, "admin");
        (await noDate.Should().ThrowAsync<InterimRateFormatException>()).Which.Message.Should().Contain("effective date");

        var noRate = () => Service.ImportAsync(Csv("NPI,Effective Date\n1234567893,09/01/2026\n"), "x.csv", Write, Tenant, "admin");
        (await noRate.Should().ThrowAsync<InterimRateFormatException>()).Which.Message.Should().Contain("no inpatient or outpatient");
    }

    [Fact]
    public async Task A_rate_above_100_percent_is_loaded_with_a_warning()
    {
        var report = await Service.ImportAsync(
            Csv("NPI,Inpatient,Effective Date\n1234567893,1.15,09/01/2026\n"), "x.csv", new InterimRateImportOptions(), Tenant, "admin");

        report.Warnings.Should().ContainSingle().Which.Should().Contain("above 100%");
    }

    [Fact]
    public async Task The_header_can_sit_below_a_title()
    {
        var text = "Texas Hospital Interim Rates\nAs of September 2026\n\n" + Sheet;

        var report = await Service.ImportAsync(Csv(text), "interim.csv", new InterimRateImportOptions(), Tenant, "admin");

        report.Rows.Should().HaveCount(2);
    }
}
