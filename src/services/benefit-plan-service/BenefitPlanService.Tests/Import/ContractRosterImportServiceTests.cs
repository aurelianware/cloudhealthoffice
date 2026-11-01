using System.Text;
using BenefitPlanService.Import.Contracts;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// Provider contract roster CSV → engine contracts with reimbursement terms,
/// a pricing copy tagged import:{source}, priced by the engine against
/// imported fee schedules.
/// </summary>
public sealed class ContractRosterImportServiceTests
{
    private const string Tenant = "tenant-a";
    private static readonly Guid PlanGuid = Guid.Parse("7d4f2a9e-3b1c-4e5f-9a8b-1c2d3e4f5a6b");
    private const string ContractedNpi = "1245319599";
    private const string HospitalNpi = "1003000126";
    private const string ContractSchedule = "tenant-a:acme-pcp-rates:20260101";
    private const string MedicareSchedule = "tenant-a:medicare-pfs-2026-04412-18:20260101";

    private static string Roster(string extra = "") =>
        "NPI,Plan,Network Status,Effective Date,Term Date,Contract Number,Method,Fee Schedule ID,Percent,Code From,Code To,Claim Type\n" +
        $"{ContractedNpi},STAR-TX-01,in,01/01/2026,,C-100,fee_schedule,{ContractSchedule},,99202,99499,professional\n" +
        $"{ContractedNpi},STAR-TX-01,in,01/01/2026,,C-100,percent_of_medicare,{MedicareSchedule},110,,,professional\n" +
        $"{HospitalNpi},{PlanGuid},par,2026-01-01,2026-12-31,H-7,percent_of_billed,,55,,,institutional\n" +
        extra;

    [Fact]
    public async Task DryRun_GroupsRowsIntoContracts_WritesNothing()
    {
        var (svc, contracts, _) = await Build();

        var report = await svc.ImportAsync(Csv(Roster()), Options(), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.Origin.Should().Be("import:qnxt");
        report.Columns["npi"].Should().Be("NPI");
        report.Contracts.Should().Be(2);
        report.Terms.Should().Be(3);
        report.Outcomes[ContractImportOutcome.WouldCreate].Should().Be(2);
        report.MissingFeeSchedules.Should().BeEmpty();
        contracts.Writes.Should().Be(0);

        var pcp = report.ContractsSample.Single(c => c.ProviderNpi == ContractedNpi);
        pcp.Id.Should().Be($"{Tenant}:{ContractedNpi}:{PlanGuid:D}", "the plan code resolves to the plan's id, as claims carry it");
        pcp.NetworkStatus.Should().Be(NetworkStatus.InNetwork);
        pcp.ContractNumber.Should().Be("C-100");
        pcp.ReimbursementTerms.Select(t => t.Method).Should().Equal(
            ReimbursementMethod.FeeScheduleRef, ReimbursementMethod.PercentOfMedicare);
        pcp.ReimbursementTerms[0].Scope.ProcedureCodeFrom.Should().Be("99202");
        pcp.ReimbursementTerms[1].Percent.Should().Be(1.10m);
        pcp.ReimbursementTerms[1].BaseScheduleId.Should().Be(MedicareSchedule);

        var hospital = report.ContractsSample.Single(c => c.ProviderNpi == HospitalNpi);
        hospital.NetworkStatus.Should().Be(NetworkStatus.Participating);
        hospital.TermDate.Should().Be(new DateTime(2026, 12, 31));
        hospital.ReimbursementTerms.Single().Scope.ClaimType.Should().Be(ContractClaimType.Institutional);
    }

    [Fact]
    public async Task RealRun_ContractedLinesPriceFromTheirTerms_UncontractedFromThePlanDefault()
    {
        var (svc, contracts, schedules) = await Build();

        var report = await svc.ImportAsync(Csv(Roster()), Options(dryRun: false), Tenant, "admin");
        report.Outcomes[ContractImportOutcome.Created].Should().Be(2);
        contracts.Stored.Should().OnlyContain(c => c.Origin == "import:qnxt");

        var engine = new RateResolutionService(schedules, contracts, NullLogger<RateResolutionService>.Instance);
        PricingRequest Line(string npi, string code, string pos = "11") => new()
        {
            TenantId = Tenant, PlanId = PlanGuid.ToString("D"), ProviderNpi = npi, ProcedureCode = code,
            PlaceOfServiceCode = pos, ServiceDate = new DateTime(2026, 3, 1), BilledAmount = 200m,
        };

        var office = await engine.ResolveAsync(Line(ContractedNpi, "99213"));
        office.AllowedAmount.Should().Be(75.00m, "the E/M range term names the contract's own schedule");
        office.NetworkStatus.Should().Be(NetworkStatus.InNetwork);

        var xray = await engine.ResolveAsync(Line(ContractedNpi, "71046"));
        xray.AllowedAmount.Should().Be(44.00m, "outside the E/M range the 110%-of-Medicare term applies: 40.00 × 1.10");

        var uncontracted = await engine.ResolveAsync(Line("1316250707", "99213"));
        uncontracted.AllowedAmount.Should().Be(55.00m, "no contract: the plan's default schedule");
        uncontracted.RateSource.Should().Be(RateSource.PlanDefault);
    }

    [Fact]
    public async Task Reimport_IsUnchanged_ChangedRowReplaces_OtherOriginsAreProtected()
    {
        var (svc, contracts, _) = await Build();
        await svc.ImportAsync(Csv(Roster()), Options(dryRun: false), Tenant, "admin");

        var again = await svc.ImportAsync(Csv(Roster()), Options(dryRun: false), Tenant, "admin");
        again.Outcomes[ContractImportOutcome.Unchanged].Should().Be(2);

        var changed = await svc.ImportAsync(Csv(Roster().Replace(",55,", ",60,")), Options(dryRun: false), Tenant, "admin");
        changed.Outcomes[ContractImportOutcome.Replaced].Should().Be(1);
        contracts.Stored.Single(c => c.ProviderNpi == HospitalNpi).ReimbursementTerms.Single().Percent.Should().Be(0.60m);

        // A contract written by the backfill or the contracts service is not this importer's to replace.
        var manual = contracts.Stored.Single(c => c.ProviderNpi == HospitalNpi);
        manual.Origin = "engine-only";
        var protectedRun = await svc.ImportAsync(Csv(Roster().Replace(",60,", ",65,").Replace(",55,", ",65,")), Options(dryRun: false), Tenant, "admin");
        protectedRun.Outcomes[ContractImportOutcome.Protected].Should().Be(1);
        protectedRun.Warnings.Should().Contain(w => w.Contains("replaceNonImported"));
    }

    [Fact]
    public async Task BadRows_AreRejectedWithReasons_GoodRowsStillLoad()
    {
        var (svc, _, _) = await Build();
        var extra =
            "1234567890,STAR-TX-01,in,01/01/2026,,,fee_schedule,x,,,,\n" +              // bad NPI check digit
            "1316250707,NO-SUCH-PLAN,in,01/01/2026,,,fee_schedule,x,,,,\n" +
            "1316250707,STAR-TX-01,maybe,01/01/2026,,,fee_schedule,x,,,,\n" +
            "1316250707,STAR-TX-01,in,01/01/2026,,,percent_of_medicare,x,1.10,,,\n" +   // ratio, not a percentage
            "1316250707,STAR-TX-01,in,01/01/2026,,,capitation,,,,,\n" +
            "1316250707,STAR-TX-01,in,01/01/2026,,,fee_schedule,tenant-a:missing:20260101,,,,\n";

        var report = await svc.ImportAsync(Csv(Roster(extra)), Options(), Tenant, "admin");

        report.RowsRejected.Should().Be(5);
        report.RowErrors.Select(e => e.Error).Should().Contain(e => e.Contains("valid 10-digit NPI"))
            .And.Contain(e => e.Contains("NO-SUCH-PLAN"))
            .And.Contain(e => e.Contains("network_status"))
            .And.Contain(e => e.Contains("110 = 110%"))
            .And.Contain(e => e.Contains("capitation"));
        report.MissingFeeSchedules.Should().Equal("tenant-a:missing:20260101");
        report.Warnings.Should().Contain(w => w.Contains("will pend"));
        report.Contracts.Should().Be(3);
    }

    [Fact]
    public async Task MissingRequiredColumn_IsNamed()
    {
        var (svc, _, _) = await Build();
        var act = () => svc.ImportAsync(Csv("NPI,Plan,Method\n1245319599,STAR-TX-01,fee_schedule\n"), Options(), Tenant, "admin");
        (await act.Should().ThrowAsync<FeeScheduleFormatException>()).WithMessage("*effective*");
    }

    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    public void IsValidNpi_ChecksLuhn(string npi, bool valid)
        => ContractRosterImportService.IsValidNpi(npi).Should().Be(valid);

    // ── helpers ──

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private static ContractRosterImportOptions Options(bool dryRun = true) => new() { Source = "QNXT", DryRun = dryRun };

    private static async Task<(ContractRosterImportService, InMemoryProviderContractRepository, InMemoryFeeScheduleRepository)> Build()
    {
        var contracts = new InMemoryProviderContractRepository();
        var schedules = new InMemoryFeeScheduleRepository();
        var plans = new InMemoryBenefitPlanRepository();
        await plans.CreateAsync(new BenefitPlan
        {
            Id = PlanGuid.ToString("D"), TenantId = Tenant, PlanId = "STAR-TX-01", PlanName = "STAR test plan",
            EffectiveDate = new DateTime(2026, 1, 1), VersionState = PlanVersionState.Published,
        });

        var jan1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await schedules.UpsertAsync(new FeeSchedule
        {
            Id = ContractSchedule, TenantId = Tenant, Name = "Acme PCP rates", Type = FeeScheduleType.Commercial, EffectiveDate = jan1,
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 75m }],
        });
        await schedules.UpsertAsync(new FeeSchedule
        {
            Id = MedicareSchedule, TenantId = Tenant, Name = "Medicare", Type = FeeScheduleType.MedicareMpfs, EffectiveDate = jan1,
            Lines = [new FeeScheduleLine { ProcedureCode = "71046", RateType = FeeScheduleRateType.FlatRate, Rate = 40m }],
        });
        await schedules.UpsertAsync(new FeeSchedule
        {
            Id = $"{Tenant}:plan-default:{PlanGuid:D}", TenantId = Tenant, Name = "Plan default", Type = FeeScheduleType.Custom,
            DefaultForPlanId = PlanGuid.ToString("D"), EffectiveDate = jan1,
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 55m }],
        });

        return (new ContractRosterImportService(contracts, schedules, plans, NullLogger<ContractRosterImportService>.Instance),
            contracts, schedules);
    }
}
