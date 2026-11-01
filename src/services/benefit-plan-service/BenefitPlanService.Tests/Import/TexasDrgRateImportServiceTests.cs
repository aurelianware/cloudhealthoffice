using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Import.MedicaidDrg;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using FluentAssertions;
using Xunit;

namespace BenefitPlanService.Tests.Import;

public class TexasDrgRateImportServiceTests
{
    private const string Tenant = "tenant-a";
    private readonly InMemoryMedicaidDrgRateRepository _store = new();
    private TexasDrgRateImportService Service => new(_store);

    private static TexasDrgUpload Weights(string effective = "09/01/2026", string mean = "10,161.08", string weight1 = "8.4410") =>
        new("tx-apr-drg-grouper-current-ver.pdf", TexasDrgWeightFileTests.WeightPdf(TexasDrgWeightFileTests.TitleFor(effective, mean), weight1));

    private static TexasDrgUpload Urban() =>
        new("2027-fy-hosp-urban-sda.pdf", TexasSdaFileTests.Pdf(TexasSdaFileTests.UrbanTitle, TexasSdaFileTests.Waco, TexasSdaFileTests.Houston));

    private static TexasDrgUpload Rural() =>
        new("2027-fy-hosp-rural-sda.pdf", TexasSdaFileTests.Pdf(TexasSdaFileTests.RuralTitle, TexasSdaFileTests.Andrews));

    private static readonly TexasDrgImportOptions Write = new() { DryRun = false };

    [Fact]
    public async Task A_dry_run_reports_every_file_and_writes_nothing()
    {
        var report = await Service.ImportAsync([Weights(), Urban(), Rural()], new TexasDrgImportOptions(), Tenant, "admin");

        report.DryRun.Should().BeTrue();
        report.Files.Should().HaveCount(3);
        report.Files.Should().OnlyContain(f => f.Outcome == FeeScheduleImportOutcome.WouldWrite);
        report.Files.Single(f => f.Kind == "weights").Rows.Should().Be(4);
        report.Files.Single(f => f.Kind == "Urban").Rows.Should().Be(2);
        report.Warnings.Should().BeEmpty();
        _store.WeightTables.Should().BeEmpty();
        _store.HospitalTables.Should().BeEmpty();
    }

    [Fact]
    public async Task Writing_loads_tables_the_pricer_can_read_by_discharge_date()
    {
        await Service.ImportAsync([Weights(), Urban(), Rural()], Write, Tenant, "admin");

        var weights = await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2026, 10, 15));
        weights!.GrouperVersion.Should().Be("40");
        weights.Find("001-3")!.RelativeWeight.Should().Be(11.4183m);
        (await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2026, 8, 31))).Should().BeNull();

        var match = (await _store.FindHospitalAsync(Tenant, "TX", "1174563779", new DateTime(2026, 10, 15))).Single();
        var hospital = match.ToPricerHospital(0.4m);
        hospital.TexasClass.Should().Be(TexasHospitalClass.Rural);
        hospital.BaseRate.Should().Be(17873.49m);
        hospital.DeliveryBaseRate.Should().Be(23188.77m);

        // End to end: the stored inputs price a stay at that rural hospital.
        var stay = new MedicaidDrgStay { DrgSoi = "001-1", LengthOfStayDays = 3, AllowedCharges = 50000m, AgeYearsAtAdmission = 40 };
        var price = new TexasMedicaidDrgPricer(new TexasDrgPolicy { UniversalMean = weights.UniversalMean })
            .Price(stay, weights.Find("001-1"), hospital);
        price.AllowedAmount.Should().Be(150870.13m); // 17,873.49 × 8.4410
    }

    [Fact]
    public async Task Loading_the_same_files_again_changes_nothing_and_different_content_needs_replace()
    {
        await Service.ImportAsync([Weights()], Write, Tenant, "admin");

        var again = await Service.ImportAsync([Weights()], Write, Tenant, "admin");
        again.Files.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Unchanged);

        var changed = await Service.ImportAsync([Weights(weight1: "9.0000")], Write, Tenant, "admin");
        changed.Files.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Exists);
        (await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2026, 10, 1)))!.Find("001-1")!.RelativeWeight.Should().Be(8.4410m);

        var replaced = await Service.ImportAsync([Weights(weight1: "9.0000")], Write with { Replace = true }, Tenant, "admin");
        replaced.Files.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Replaced);
        (await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2026, 10, 1)))!.Find("001-1")!.RelativeWeight.Should().Be(9.0000m);
    }

    [Fact]
    public async Task Each_year_keeps_its_own_table()
    {
        await Service.ImportAsync([Weights("09/01/2025", weight1: "7.0000"), Weights("09/01/2026")], Write, Tenant, "admin");

        (await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2026, 3, 1)))!.Find("001-1")!.RelativeWeight.Should().Be(7.0000m);
        (await _store.GetWeightTableAsync(Tenant, "TX", new DateTime(2027, 3, 1)))!.Find("001-1")!.RelativeWeight.Should().Be(8.4410m);
    }

    [Fact]
    public async Task Warns_when_universal_means_disagree()
    {
        var report = await Service.ImportAsync([Weights(mean: "9,999.99"), Urban()], new TexasDrgImportOptions(), Tenant, "admin");

        report.Warnings.Should().ContainSingle().Which.Should().Contain("9999.99").And.Contain("10161.08");
    }

    [Fact]
    public async Task One_bad_file_writes_nothing()
    {
        var bad = new TexasDrgUpload("notes.pdf", TexasSdaFileTests.Pdf(["Some other report"], TexasSdaFileTests.Waco));

        var act = () => Service.ImportAsync([Weights(), bad], Write, Tenant, "admin");

        await act.Should().ThrowAsync<TexasSdaFormatException>();
        _store.WeightTables.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenants_do_not_see_each_others_tables()
    {
        await Service.ImportAsync([Weights()], Write, Tenant, "admin");

        (await _store.GetWeightTableAsync("tenant-b", "TX", new DateTime(2026, 10, 1))).Should().BeNull();
    }

    /// <summary>
    /// Loads every real HHSC file in CHO_TX_SDA_DIR (and its weights folder)
    /// when set; skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Loads_the_published_files_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("CHO_TX_SDA_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var paths = Directory.GetFiles(dir, "*.pdf").Where(p => p.Contains("sda") || p.Contains("apr-drg"))
            .Concat(Directory.Exists(Path.Combine(dir, "weights")) ? Directory.GetFiles(Path.Combine(dir, "weights")) : []);
        var uploads = paths.Select(p => new TexasDrgUpload(Path.GetFileName(p), File.OpenRead(p))).ToList();

        var report = await Service.ImportAsync(uploads, Write, Tenant, "admin");

        foreach (var f in report.Files.OrderBy(f => f.EffectiveDate))
            Console.WriteLine($"{f.EffectiveDate:yyyy-MM-dd} {f.Kind,-8} V{f.GrouperVersion} rows {f.Rows} held {f.HeldBack.Count} {f.Outcome} ({f.FileName})");
        foreach (var w in report.Warnings) Console.WriteLine("warning: " + w);
        report.Files.Should().NotBeEmpty();
    }
}
