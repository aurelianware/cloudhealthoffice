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
    private static readonly Guid SecondPlanGuid = Guid.Parse("2a6c1e0f-8d3b-4c7a-b5e9-0f1a2b3c4d5e");
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
        pcp.Id.Should().Be($"{Tenant}:{ContractedNpi}:STAR-TX-01", "a plan code keys the contract to every version of the plan");
        pcp.PlanId.Should().Be("STAR-TX-01");
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
            TenantId = Tenant, PlanId = PlanGuid.ToString("D"), PlanCode = "STAR-TX-01", ProviderNpi = npi, ProcedureCode = code,
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

    [Fact]
    public async Task PlanCodeContract_StillPricesAfterThePlanIsAmended()
    {
        var (svc, contracts, schedules) = await Build();
        await svc.ImportAsync(Csv(Roster()), Options(dryRun: false), Tenant, "admin");

        // An amendment publishes version 2 under a new id; claims now carry that id.
        var amendedVersionId = Guid.NewGuid().ToString("D");
        var engine = new RateResolutionService(schedules, contracts, NullLogger<RateResolutionService>.Instance);
        PricingRequest Line(string npi, string? planCode) => new()
        {
            TenantId = Tenant, PlanId = amendedVersionId, PlanCode = planCode, ProviderNpi = npi, ProcedureCode = "99213",
            PlaceOfServiceCode = "11", ServiceDate = new DateTime(2026, 3, 1), BilledAmount = 200m,
        };

        (await engine.ResolveAsync(Line(ContractedNpi, "STAR-TX-01"))).AllowedAmount
            .Should().Be(75.00m, "the contract was imported by plan code, so it follows the plan to version 2");
        (await engine.ResolveAsync(Line(ContractedNpi, null))).AllowedAmount
            .Should().NotBe(75.00m, "without the plan code only version-keyed contracts are found");

        // A contract keyed to the new version wins over the plan-code one.
        await contracts.UpsertAsync(new ProviderContract
        {
            Id = ProviderContract.MakeId(Tenant, ContractedNpi, amendedVersionId), TenantId = Tenant,
            ProviderNpi = ContractedNpi, PlanId = amendedVersionId, EffectiveDate = new DateTime(2026, 1, 1),
            NetworkStatus = NetworkStatus.InNetwork, Origin = "import:test",
            ReimbursementTerms = [new ReimbursementTerm { Id = "v2", Method = ReimbursementMethod.PercentOfBilled, Percent = 0.5m }],
        });
        (await engine.ResolveAsync(Line(ContractedNpi, "STAR-TX-01"))).AllowedAmount
            .Should().Be(100.00m, "the version's own contract (50% of billed 200) is used first");
    }


    // ── CHO as system of record: legal contracts in provider-contracts-service ──

    private static ContractRosterImportOptions ChoOptions(bool dryRun = true) =>
        new() { Source = "QNXT", DryRun = dryRun, SystemOfRecord = ContractSystemOfRecord.Cho };

    private const string ChoRoster =
        "NPI,Provider Name,Provider Type,Plan,Network Status,Effective Date,Contract Number,LOB,Method,Fee Schedule ID,Percent,Code From,Code To\n" +
        $"{ContractedNpi},Acme Family Clinic,organization,STAR-TX-01,in,01/01/2026,C-100,Medicaid,fee_schedule,{ContractSchedule},,99202,99499\n" +
        $"{ContractedNpi},Acme Family Clinic,organization,STAR-TX-01,in,01/01/2026,C-100,Medicaid,percent_of_medicare,{MedicareSchedule},110,,\n";

    [Fact]
    public async Task Cho_RealRun_CreatesActivatesAndPricesFromTheProjection()
    {
        var legal = new FakeProviderContractsClient();
        var (svc, contracts, schedules) = await Build(legal);

        var report = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(dryRun: false), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.SystemOfRecord.Should().Be(ContractSystemOfRecord.Cho);
        report.LegalContracts.Should().ContainSingle(l => l.ContractNumber == "C-100" && l.Outcome == "Created");
        legal.Calls.Should().Equal("get C-100", "create C-100", "terms legal-C-100", "activate legal-C-100", "projection legal-C-100");
        legal.Calls.Should().NotContain(c => c.StartsWith("put "), "the importer never replaces the whole contract (its TIN is masked on read)");

        var created = legal.ByNumber["C-100"];
        created.ProviderName.Should().Be("Acme Family Clinic");
        created.ProviderType.Should().Be(LegalProviderType.Organization);
        created.LineOfBusiness.Should().Be(LegalLineOfBusiness.Medicaid);
        created.PlanIds.Should().Equal("STAR-TX-01");
        created.Status.Should().Be(LegalContractStatus.Active);
        created.ReimbursementTerms.Should().HaveCount(2);

        var stored = contracts.Stored.Should().ContainSingle().Subject;
        stored.Origin.Should().Be("legal");
        stored.LegalContractId.Should().Be("legal-C-100");

        var engine = new RateResolutionService(schedules, contracts, NullLogger<RateResolutionService>.Instance);
        var xray = await engine.ResolveAsync(new PricingRequest
        {
            TenantId = Tenant, PlanId = PlanGuid.ToString("D"), PlanCode = "STAR-TX-01", ProviderNpi = ContractedNpi, ProcedureCode = "71046",
            PlaceOfServiceCode = "11", ServiceDate = new DateTime(2026, 3, 1), BilledAmount = 200m,
        });
        xray.AllowedAmount.Should().Be(44.00m, "the CHO contract's 110%-of-Medicare term prices it");

        // Re-running the same roster changes nothing.
        legal.Calls.Clear();
        var again = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(dryRun: false), Tenant, "admin");
        again.LegalContracts.Single().Outcome.Should().Be("Unchanged");
        again.Outcomes[ContractImportOutcome.Unchanged].Should().Be(1);
        legal.Calls.Should().Equal("get C-100", "projection legal-C-100");
    }

    [Fact]
    public async Task Cho_DryRun_OnlyLooksUpContracts()
    {
        var legal = new FakeProviderContractsClient();
        var (svc, contracts, _) = await Build(legal);

        var report = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(), Tenant, "admin");

        report.LegalContracts.Single().Outcome.Should().Be("WouldCreate");
        legal.Calls.Should().Equal("get C-100");
        contracts.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Cho_RequiresAContractNumber()
    {
        var (svc, _, _) = await Build(new FakeProviderContractsClient());
        var roster = "NPI,Plan,Network,Effective Date,Method,Percent\n" + $"{HospitalNpi},STAR-TX-01,par,2026-01-01,percent_of_billed,55\n";

        var report = await svc.ImportAsync(Csv(roster), ChoOptions(), Tenant, "admin");

        report.RowsRejected.Should().Be(1);
        report.LegalContracts.Should().BeEmpty();
    }

    [Fact]
    public async Task Cho_RejectsANumberThatIsAnotherProvidersContract()
    {
        var legal = new FakeProviderContractsClient();
        legal.ByNumber["C-100"] = new LegalContractDto
        {
            Id = "legal-other", ContractNumber = "C-100", ProviderNpi = HospitalNpi, Status = LegalContractStatus.Active,
        };
        var (svc, contracts, _) = await Build(legal);

        var report = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(dryRun: false), Tenant, "admin");

        var result = report.LegalContracts.Single();
        result.Outcome.Should().Be("Rejected");
        result.Detail.Should().Contain(HospitalNpi);
        legal.Calls.Should().Equal("get C-100");
        contracts.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Cho_RejectsPlansWithDifferentTermsUnderOneNumber()
    {
        var legal = new FakeProviderContractsClient();
        var (svc, _, _) = await Build(legal);
        var roster =
            "NPI,Plan,Network,Effective Date,Contract Number,Method,Percent\n" +
            $"{HospitalNpi},STAR-TX-01,par,2026-01-01,H-7,percent_of_billed,55\n" +
            $"{HospitalNpi},STAR-TX-02,par,2026-01-01,H-7,percent_of_billed,60\n";

        var report = await svc.ImportAsync(Csv(roster), ChoOptions(dryRun: false), Tenant, "admin");

        var result = report.LegalContracts.Single();
        result.Outcome.Should().Be("Rejected");
        result.Detail.Should().Contain("different terms");
        legal.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cho_LeavesAnotherLegalContractsPricingCopyAlone()
    {
        var legal = new FakeProviderContractsClient();
        var (svc, contracts, _) = await Build(legal);
        await contracts.UpsertAsync(new ProviderContract
        {
            Id = ProviderContract.MakeId(Tenant, ContractedNpi, "STAR-TX-01"), TenantId = Tenant,
            ProviderNpi = ContractedNpi, PlanId = "STAR-TX-01", Origin = "legal", LegalContractId = "legal-older",
            EffectiveDate = new DateTime(2025, 1, 1),
        });

        var report = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(dryRun: false), Tenant, "admin");

        report.Outcomes[ContractImportOutcome.Protected].Should().Be(1);
        contracts.Stored.Single().LegalContractId.Should().Be("legal-older");
    }

    [Fact]
    public async Task Cho_ContractsServiceRefusal_IsReportedNotThrown()
    {
        var legal = new FakeProviderContractsClient { FailCreate = true };
        var (svc, contracts, _) = await Build(legal);

        var report = await svc.ImportAsync(Csv(ChoRoster), ChoOptions(dryRun: false), Tenant, "admin");

        var result = report.LegalContracts.Single();
        result.Outcome.Should().Be("Failed");
        result.Detail.Should().Contain("contracts:write");
        contracts.Writes.Should().Be(0);
    }

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private static ContractRosterImportOptions Options(bool dryRun = true) => new() { Source = "QNXT", DryRun = dryRun };

    private static async Task<(ContractRosterImportService, InMemoryProviderContractRepository, InMemoryFeeScheduleRepository)> Build(
        IProviderContractsClient? legal = null)
    {
        var contracts = new InMemoryProviderContractRepository();
        var schedules = new InMemoryFeeScheduleRepository();
        var plans = new InMemoryBenefitPlanRepository();
        await plans.CreateAsync(new BenefitPlan
        {
            Id = PlanGuid.ToString("D"), TenantId = Tenant, PlanId = "STAR-TX-01", PlanName = "STAR test plan",
            EffectiveDate = new DateTime(2026, 1, 1), VersionState = PlanVersionState.Published,
        });
        await plans.CreateAsync(new BenefitPlan
        {
            Id = SecondPlanGuid.ToString("D"), TenantId = Tenant, PlanId = "STAR-TX-02", PlanName = "Second STAR plan",
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

        return (new ContractRosterImportService(contracts, schedules, plans, NullLogger<ContractRosterImportService>.Instance, legal),
            contracts, schedules);
    }
}
