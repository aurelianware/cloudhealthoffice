using System.IO.Compression;
using System.Text;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// CMS PFS RVU files (PPRRVU + GPCI) into one MedicareMpfs schedule per
/// locality in the store claims are priced from, priced by the engine.
/// RVU values below are synthetic, in the CMS file layout.
/// </summary>
public sealed class MedicarePfsImportServiceTests
{
    private const string Tenant = "tenant-a";
    private const decimal Cf = 33.4009m;

    // 31 columns, CMS order: HCPCS, MOD, DESCRIPTION, STATUS, NOT USED, WORK, NONFAC PE, NONFAC NA,
    // FAC PE, FAC NA, MP, NONFAC TOTAL, FAC TOTAL, PCTC, GLOB, PRE, INTRA, POST, MULT PROC, BILAT,
    // ASST, CO, TEAM, ENDO, CONV FACTOR, SUPERVISION, CALC FLAG, DI FAMILY, NONFAC PE OPPS, FAC PE OPPS, MP OPPS
    private static string Row(string code, string mod, string status, decimal work, decimal nonFacPe, string nonFacNa,
        decimal facPe, string facNa, decimal mp, string multProc = "0", string bilat = "0", string asst = "0", decimal cf = Cf)
        => string.Join(",", code, mod, "\"synthetic\"", status, "", work, nonFacPe, nonFacNa, facPe, facNa, mp,
            work + nonFacPe + mp, work + facPe + mp, "0", "000", "0", "0", "0", multProc, bilat, asst, "0", "0", "",
            cf, "09", "", "88", "0", "0", "0");

    private static string Pprrvu(decimal cf = Cf, string extra = "") =>
        "CPT codes and descriptions only are copyright AMA (synthetic test file)\n" +
        ",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,\n" +
        "HCPCS,MOD,DESCRIPTION,CODE,PAYMENT,WORK,NON-FAC,NON-FAC,FACILITY,FACILITY,MP,NON-FACILITY,FACILITY,PCTC,GLOB,PRE,INTRA,POST,MULT,BILAT,ASST,CO-,TEAM,ENDO,CONV,PHYSICIAN,CALCULATION,DIAGNOSTIC IMAGING,NON-FAC PE,FACILITY PE,MP\n" +
        ",,,STATUS,POLICY INDICATOR,RVU,PE RVU,NA INDICATOR,PE RVU,NA INDICATOR,RVU,TOTAL,TOTAL,IND,DAYS,OP,OP,OP,PROC,SURG,SURG,SURG,SURG,BASE,FACTOR,SUPERVISION,FLAG,FAMILY INDICATOR,USED FOR OPPS,USED FOR OPPS,USED FOR OPPS\n" +
        Row("99213", "", "A", 1.30m, 1.20m, "", 0.55m, "", 0.10m, cf: cf) + "\n" +
        Row("71046", "", "A", 0.22m, 0.80m, "", 0.80m, "", 0.02m, multProc: "4", cf: cf) + "\n" +
        Row("71046", "26", "A", 0.22m, 0.08m, "", 0.08m, "", 0.01m, multProc: "4", cf: cf) + "\n" +
        Row("71046", "TC", "A", 0.00m, 0.72m, "", 0.72m, "", 0.01m, multProc: "4", cf: cf) + "\n" +
        Row("27447", "", "A", 19.60m, 0.00m, "NA", 11.10m, "", 4.00m, multProc: "2", bilat: "1", asst: "2", cf: cf) + "\n" +
        Row("99091", "", "B", 1.10m, 0.40m, "", 0.40m, "", 0.07m, cf: cf) + "\n" +
        Row("0001U", "", "X", 0m, 0m, "", 0m, "", 0m, cf: cf) + "\n" +
        Row("J3490", "", "A", 0m, 0m, "", 0m, "", 0m, cf: cf) + "\n" +
        extra;

    private const string Gpci =
        "ADDENDUM E. FINAL CY 2026 GEOGRAPHIC PRACTICE COST INDICES (GPCIs) BY STATE AND MEDICARE LOCALITY\n" +
        "\n" +
        "Medicare Administrative Contractor (MAC),State,Locality Number,Locality Name,2026 PW GPCI (without 1.0 Floor),2026 PW GPCI (with 1.0 Floor),2026 PE GPCI,2026 MP GPCI\n" +
        "04412,TX,18,DALLAS,1.010,1.010,1.010,0.800\n" +
        "04412,TX,09,BRAZORIA,0.990,1.000,0.950,0.900\n" +
        "01182,CA,26,LOS ANGELES,1.050,1.050,1.200,0.700\n" +
        "* Footnote: synthetic values\n";

    [Fact]
    public void ReadPprrvu_KeepsPayableRows_AppliesNa_AndReadsIndicators()
    {
        var file = MedicarePfsFiles.ReadPprrvu(Csv(Pprrvu()));

        file.RowsRead.Should().Be(8);
        file.Lines.Select(l => (l.ProcedureCode, l.Modifier)).Should().Equal(
            ("27447", null), ("71046", null), ("71046", "26"), ("71046", "TC"), ("99213", null));
        file.Lines.Should().OnlyContain(l => l.RateType == FeeScheduleRateType.Rvu);
        file.ConversionFactors.Should().Equal(Cf);

        var knee = file.Lines.Single(l => l.ProcedureCode == "27447");
        knee.PeRvu.Should().Be(11.10m, "non-facility NA prices the office setting at the facility PE");
        knee.MultipleProcedureIndicator.Should().Be(MultipleProcedureIndicator.StandardSurgery);
        knee.BilateralAdjustmentApplies.Should().BeTrue();
        file.Lines.Single(l => l.ProcedureCode == "99213").BilateralAdjustmentApplies.Should().BeFalse();
        file.NonFacilityNotApplicable.Should().Be(1);

        file.Skipped.Keys.Should().Contain("status B is not paid under the fee schedule")
            .And.Contain("status X is not paid under the fee schedule")
            .And.Contain("no RVUs (carrier or contractor priced)");
    }

    [Fact]
    public void ReadGpci_FindsHeaderBelowTitle_PrefersFlooredWorkGpci()
    {
        var localities = MedicarePfsFiles.ReadGpci(Csv(Gpci));

        localities.Select(l => l.Key).Should().Equal("04412-18", "04412-09", "01182-26");
        var brazoria = localities.Single(l => l.Key == "04412-09");
        brazoria.WorkGpci.Should().Be(1.000m);
        brazoria.State.Should().Be("TX");
        brazoria.Name.Should().Be("BRAZORIA");
    }

    [Fact]
    public async Task DryRun_ByState_ReportsEachLocality_WritesNothing()
    {
        var (svc, repo) = Build();

        var report = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci), Options(states: ["TX"]), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.ConversionFactor.Should().Be(Cf);
        report.LinesPerSchedule.Should().Be(5);
        report.Localities.Select(l => l.Locality).Should().Equal("04412-18", "04412-09");
        report.Localities.Should().OnlyContain(l => l.Outcome == FeeScheduleImportOutcome.WouldWrite);
        report.Localities[0].ScheduleId.Should().Be("tenant-a:medicare-pfs-2026-04412-18:20260101");
        // (1.30 × 1.010 + 1.20 × 1.010 + 0.10 × 0.800) × 33.4009 = 2.605 × 33.4009 = 87.01
        report.Localities[0].Sample99213NonFacility.Should().Be(87.01m);
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task RealRun_WritesMedicareMpfsSchedules_TheEnginePricesByLocalityAndSetting()
    {
        var (svc, repo) = Build();

        var report = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci),
            Options(localities: ["04412-18", "01182-26"], dryRun: false), Tenant, "admin");

        report.Localities.Should().OnlyContain(l => l.Outcome == FeeScheduleImportOutcome.Created);
        var dallas = repo.Stored.Single(s => s.Locality == "04412-18");
        dallas.Type.Should().Be(FeeScheduleType.MedicareMpfs);
        dallas.SourceType.Should().Be(FeeScheduleSourceType.PublicGovernment);
        dallas.ConversionFactor.Should().Be(Cf);
        (dallas.WorkGpci, dallas.PeGpci, dallas.MpGpci).Should().Be((1.010m, 1.010m, 0.800m));
        dallas.Jurisdiction.Should().Be("TX");

        // A Medicaid schedule at 72% of Medicare, based on the Dallas schedule.
        await repo.UpsertAsync(new FeeSchedule
        {
            Id = "tenant-a:tx-medicaid-pct:20260101", TenantId = Tenant, Name = "72% of Medicare",
            Type = FeeScheduleType.Medicaid, PercentOfMedicare = 0.72m, BaseMpfsFeeScheduleId = dallas.Id,
            DefaultForPlanId = "plan-1", EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 0.72m }],
        });

        var engine = Engine(repo);
        var office = await engine.ResolveAsync(Line("99213", "11", "plan-1"));
        office.RateSource.Should().Be(RateSource.Medicaid);
        office.AllowedAmount.Should().Be(62.65m, "72% of the Dallas non-facility 87.01");

        var facility = await engine.ResolveAsync(Line("99213", "22", "plan-1"));
        // (1.30 × 1.010 + 0.55 × 1.010 + 0.10 × 0.800) × 33.4009 = 1.9485 × 33.4009 = 65.08; × 0.72 = 46.86
        facility.AllowedAmount.Should().Be(46.86m);
    }

    [Fact]
    public async Task PlanDefault_OneLocality_PricesAMedicareAdvantageLine()
    {
        var planId = Guid.NewGuid();
        var (svc, repo) = Build(planId);

        var report = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci),
            Options(localities: ["01182-26"], dryRun: false, planId: planId), Tenant, "admin");

        report.Localities.Single().ScheduleId.Should().Be($"{Tenant}:plan-default:{planId:D}");
        var result = await Engine(repo).ResolveAsync(Line("71046", "11", planId.ToString("D"), "26"));
        // 26 line: (0.22 × 1.050 + 0.08 × 1.200 + 0.01 × 0.700) × 33.4009 = 0.334 × 33.4009 = 11.16
        result.RateSource.Should().Be(RateSource.MedicareMpfs);
        result.AllowedAmount.Should().Be(11.16m);
    }

    [Fact]
    public async Task PlanDefault_WithTwoLocalities_IsRejected()
    {
        var planId = Guid.NewGuid();
        var (svc, repo) = Build(planId);

        var report = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci), Options(states: ["TX"], dryRun: false, planId: planId), Tenant, "admin");

        report.Errors.Should().Contain(e => e.Contains("exactly one locality"));
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task SeveralConversionFactors_NeedAnOverride()
    {
        var (svc, _) = Build();
        var mixed = Pprrvu(extra: Row("99214", "", "A", 1.92m, 1.60m, "", 0.80m, "", 0.14m, cf: 33.5675m) + "\n");

        var refused = await svc.ImportAsync(Csv(mixed), Csv(Gpci), Options(states: ["CA"]), Tenant, "admin");
        refused.Errors.Should().Contain(e => e.Contains("several conversion factors"));

        var ok = await svc.ImportAsync(Csv(mixed), Csv(Gpci), Options(states: ["CA"], conversionFactor: 33.5675m), Tenant, "admin");
        ok.Errors.Should().BeEmpty();
        ok.ConversionFactor.Should().Be(33.5675m);
    }

    [Fact]
    public async Task UnknownLocality_IsNamed()
    {
        var (svc, _) = Build();

        var report = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci), Options(localities: ["99999-01"]), Tenant, "admin");

        report.Errors.Should().Contain(e => e.Contains("99999-01"));
    }

    [Fact]
    public async Task Reimport_IsUnchanged_NewReleaseNeedsReplace()
    {
        var (svc, repo) = Build();
        await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci), Options(localities: ["04412-18"], dryRun: false), Tenant, "admin");

        var again = await svc.ImportAsync(Csv(Pprrvu()), Csv(Gpci), Options(localities: ["04412-18"], dryRun: false), Tenant, "admin");
        again.Localities.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Unchanged);

        var changed = Pprrvu().Replace("1.30,1.20,", "1.31,1.20,");
        var exists = await svc.ImportAsync(Csv(changed), Csv(Gpci), Options(localities: ["04412-18"], dryRun: false), Tenant, "admin");
        exists.Localities.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Exists);

        var replaced = await svc.ImportAsync(Csv(changed), Csv(Gpci), Options(localities: ["04412-18"], dryRun: false, replace: true), Tenant, "admin");
        replaced.Localities.Single().Outcome.Should().Be(FeeScheduleImportOutcome.Replaced);
        repo.Stored.Single().Lines.Single(l => l.ProcedureCode == "99213").WorkRvu.Should().Be(1.31m);
    }

    [Fact]
    public void OpenRvuZip_PicksNonQppPprrvuByDefault()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string body)
            {
                using var w = new StreamWriter(archive.CreateEntry(name).Open());
                w.Write(body);
            }
            Add("PPRRVU2026_Jan_nonQPP.csv", Pprrvu());
            Add("PPRRVU2026_Jan_QPP.csv", Pprrvu(cf: 33.5675m));
            Add("GPCI2026.csv", Gpci);
            Add("ANES2026.csv", "x");
        }
        var bytes = zip.ToArray();

        var (p, g, pName, gName) = MedicarePfsFiles.OpenRvuZip(new MemoryStream(bytes), qpp: false);
        pName.Should().Be("PPRRVU2026_Jan_nonQPP.csv");
        gName.Should().Be("GPCI2026.csv");
        MedicarePfsFiles.ReadPprrvu(p).ConversionFactors.Should().Equal(Cf);
        MedicarePfsFiles.ReadGpci(g).Should().HaveCount(3);

        MedicarePfsFiles.OpenRvuZip(new MemoryStream(bytes), qpp: true).PprrvuName.Should().Be("PPRRVU2026_Jan_QPP.csv");
    }

    [Fact]
    public void ReadPprrvu_WrongFile_SaysSo()
    {
        var act = () => MedicarePfsFiles.ReadPprrvu(Csv("Procedure Code,Fee\n99213,62.10\n"));
        act.Should().Throw<FeeScheduleFormatException>().WithMessage("*PPRRVU*");
    }

    // ── helpers ──

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private static MedicarePfsImportOptions Options(
        string[]? localities = null, string[]? states = null, bool dryRun = true, Guid? planId = null,
        bool replace = false, decimal? conversionFactor = null) => new()
    {
        Year = 2026,
        Release = "RVU26A",
        Localities = (localities ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        States = (states ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        DryRun = dryRun,
        PlanId = planId?.ToString("D"),
        Replace = replace,
        ConversionFactor = conversionFactor,
    };

    private static PricingRequest Line(string code, string pos, string planId, string? modifier = null) => new()
    {
        TenantId = Tenant, PlanId = planId, ProviderNpi = "1234567893", ProcedureCode = code,
        Modifiers = modifier is null ? [] : [modifier], PlaceOfServiceCode = pos,
        ServiceDate = new DateTime(2026, 3, 1), BilledAmount = 500m,
    };

    private static RateResolutionService Engine(IFeeScheduleRepository repo)
    {
        var contracts = new Mock<IProviderContractRepository>();
        contracts.Setup(c => c.GetContractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderContract?)null);
        return new RateResolutionService(repo, contracts.Object, NullLogger<RateResolutionService>.Instance);
    }

    private static (MedicarePfsImportService, InMemoryFeeScheduleRepository) Build(Guid? planId = null)
    {
        var repo = new InMemoryFeeScheduleRepository();
        var plans = new InMemoryBenefitPlanRepository();
        if (planId is { } id)
        {
            plans.CreateAsync(new BenefitPlan
            {
                Id = id.ToString("D"), TenantId = Tenant, PlanId = "MA-TEST", PlanName = "MA test plan",
                EffectiveDate = new DateTime(2026, 1, 1),
            }).GetAwaiter().GetResult();
        }
        return (new MedicarePfsImportService(repo, plans, NullLogger<MedicarePfsImportService>.Instance), repo);
    }
}
