using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.ReferenceData.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// The shared fee schedule library: list and open library schedules, deploy
/// one into a tenant at a percentage (priced by the engine), and publish a
/// tenant's schedule into the library. Rates are synthetic.
/// </summary>
public sealed class FeeScheduleLibraryServiceTests
{
    private const string Library = FeeScheduleLibraryService.DefaultLibraryTenantId;
    private const string Tenant = "plan-a";
    private const string PlanCode = "TX-STAR-2027";
    private const string MedicareId = Library + ":Medicare PFS 2026 TX DALLAS (18):20260101";
    private const string TmhpId = Library + ":TMHP adult acute care:20260101";

    private readonly InMemoryFeeScheduleRepository _repo = new();
    private readonly InMemoryBenefitPlanRepository _plans = new();
    private readonly FeeScheduleLibraryService _sut;

    public FeeScheduleLibraryServiceTests()
    {
        _sut = new FeeScheduleLibraryService(_repo, _plans, new ConfigurationBuilder().Build(), NullLogger<FeeScheduleLibraryService>.Instance);
        _plans.CreateAsync(new BenefitPlan
        {
            Id = Guid.NewGuid().ToString("D"), TenantId = Tenant, PlanId = PlanCode, PlanName = "STAR 2027",
            EffectiveDate = new DateTime(2027, 1, 1),
        }).GetAwaiter().GetResult();

        _repo.UpsertAsync(new FeeSchedule
        {
            Id = MedicareId, TenantId = Library, Name = "Medicare PFS 2026 TX DALLAS (18)", Type = FeeScheduleType.MedicareMpfs,
            SourceType = FeeScheduleSourceType.PublicGovernment, SourceId = "cms-pfs-rvu", SourceVersion = "RVU26A",
            Jurisdiction = "TX", Locality = "18", CodeSystem = "HCPCS", LicenseClassification = LicenseClassification.Public,
            ConversionFactor = 33.4009m, WorkGpci = 1.010m, PeGpci = 1.010m, MpGpci = 0.800m,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new() { ProcedureCode = "99213", RateType = FeeScheduleRateType.Rvu, WorkRvu = 1.30m, PeRvu = 1.20m, PeRvuFacility = 0.55m, MpRvu = 0.10m },
                new() { ProcedureCode = "71046", RateType = FeeScheduleRateType.Rvu, WorkRvu = 0.22m, PeRvu = 0.80m, MpRvu = 0.02m },
            ],
        }).GetAwaiter().GetResult();

        _repo.UpsertAsync(new FeeSchedule
        {
            Id = TmhpId, TenantId = Library, Name = "TMHP adult acute care", Type = FeeScheduleType.Medicaid,
            SourceType = FeeScheduleSourceType.PublicGovernment, SourceId = "tmhp-static", SourceVersion = "2026-10",
            Jurisdiction = "TX", CodeSystem = "Local", LicenseClassification = LicenseClassification.Public,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new() { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 33.95m, FacilityRate = 25.10m },
                new() { ProcedureCode = "T1015", RateType = FeeScheduleRateType.FlatRate, Rate = 150.00m },
            ],
        }).GetAwaiter().GetResult();
    }

    private static FeeScheduleDeployRequest Request(string source, decimal percent, bool dryRun = false, bool ack = true) => new()
    {
        SourceId = source, Percent = percent, PlanId = PlanCode, EffectiveDate = new DateTime(2027, 1, 1),
        LicenseAcknowledged = ack, DryRun = dryRun,
    };

    [Fact]
    public async Task Lists_the_library_by_state_and_opens_a_schedule_with_computed_amounts()
    {
        var list = await _sut.ListAsync(Library, "tx", default);
        list.Select(s => s.Name).Should().Equal("Medicare PFS 2026 TX DALLAS (18)", "TMHP adult acute care");

        var detail = await _sut.GetAsync(Library, MedicareId, "992", 0, 50, default);
        detail!.LineCount.Should().Be(2);
        detail.MatchingCount.Should().Be(1);
        // (1.30 × 1.010 + 1.20 × 1.010 + 0.10 × 0.800) × 33.4009 = 2.605 × 33.4009 = 87.01
        detail.Lines.Single().Amount.Should().Be(87.01m);
        // facility PE 0.55: (1.313 + 0.5555 + 0.08) × 33.4009 = 65.08
        detail.Lines.Single().FacilityAmount.Should().Be(65.08m);
    }

    [Fact]
    public async Task A_dry_run_previews_library_and_deployed_amounts_and_writes_nothing()
    {
        var report = await _sut.DeployAsync(Tenant, "admin-1", Request(MedicareId, 110m, dryRun: true, ack: false), default);

        report.Error.Should().BeNull();
        report.Outcome.Should().Be(FeeScheduleImportOutcome.WouldWrite);
        report.RequiresCptLicense.Should().BeTrue();
        report.TargetId.Should().Be($"{Tenant}:plan-default:{PlanCode}");
        var line = report.Preview.First(p => p.ProcedureCode == "99213");
        line.LibraryAmount.Should().Be(87.01m);
        line.DeployedAmount.Should().Be(95.71m, "110% of 87.01 (conversion factor 36.7410)");
        _repo.Stored.Should().NotContain(s => s.TenantId == Tenant);
    }

    [Fact]
    public async Task A_CPT_keyed_schedule_needs_the_tenants_license_confirmation()
    {
        var report = await _sut.DeployAsync(Tenant, "admin-1", Request(MedicareId, 110m, ack: false), default);

        report.Error.Should().Contain("AMA CPT license");
        _repo.Stored.Should().NotContain(s => s.TenantId == Tenant);
    }

    [Fact]
    public async Task Deploying_Medicare_at_110_percent_prices_claims_at_110_percent_of_the_locality()
    {
        var report = await _sut.DeployAsync(Tenant, "admin-1", Request(MedicareId, 110m), default);

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Created);
        var copy = _repo.Stored.Single(s => s.TenantId == Tenant);
        copy.DefaultForPlanId.Should().Be(PlanCode);
        copy.ConversionFactor.Should().Be(36.7410m);
        copy.SourceId.Should().Be($"library:{MedicareId}");
        copy.LicenseClassification.Should().Be(LicenseClassification.CustomerProvided);
        copy.EffectiveDate.Should().Be(new DateTime(2027, 1, 1));

        var priced = await Engine().ResolveAsync(Line("99213", "11"));
        // 2.605 × 36.7410 = 95.71
        priced.AllowedAmount.Should().Be(95.71m);
    }

    [Fact]
    public async Task Deploying_a_flat_schedule_scales_rates_and_facility_rates_to_cents()
    {
        await _sut.DeployAsync(Tenant, "admin-1", Request(TmhpId, 105m), default);

        var copy = _repo.Stored.Single(s => s.TenantId == Tenant);
        copy.Lines.Single(l => l.ProcedureCode == "99213").Rate.Should().Be(35.65m, "33.95 × 1.05 = 35.6475");
        copy.Lines.Single(l => l.ProcedureCode == "99213").FacilityRate.Should().Be(26.36m, "25.10 × 1.05 = 26.355");
        copy.Lines.Single(l => l.ProcedureCode == "T1015").Rate.Should().Be(157.50m);
        (await Engine().ResolveAsync(Line("T1015", "11"))).AllowedAmount.Should().Be(157.50m);
        // The library copy is untouched.
        _repo.Stored.Single(s => s.Id == TmhpId).Lines[0].Rate.Should().Be(33.95m);
    }

    [Fact]
    public async Task A_plan_that_already_has_a_default_is_left_alone_unless_replace()
    {
        await _sut.DeployAsync(Tenant, "admin-1", Request(TmhpId, 100m), default);

        var again = await _sut.DeployAsync(Tenant, "admin-1", Request(TmhpId, 90m), default);
        again.Outcome.Should().Be(FeeScheduleImportOutcome.Exists);
        _repo.Stored.Single(s => s.TenantId == Tenant).Lines[0].Rate.Should().Be(33.95m);

        var replace = Request(TmhpId, 90m);
        replace.Replace = true;
        (await _sut.DeployAsync(Tenant, "admin-1", replace, default)).Outcome.Should().Be(FeeScheduleImportOutcome.Replaced);
        _repo.Stored.Single(s => s.TenantId == Tenant).Lines[0].Rate.Should().Be(30.56m);
    }

    [Fact]
    public async Task A_standalone_deploy_needs_a_name_and_is_not_a_plan_default()
    {
        var noTarget = Request(TmhpId, 100m);
        noTarget.PlanId = null;
        (await _sut.DeployAsync(Tenant, "admin-1", noTarget, default)).Error.Should().Contain("planId");

        noTarget.Name = "TMHP adult for contracts";
        var report = await _sut.DeployAsync(Tenant, "admin-1", noTarget, default);

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Created);
        var copy = _repo.Stored.Single(s => s.TenantId == Tenant);
        copy.DefaultForPlanId.Should().BeNull();
        copy.Id.Should().Be($"{Tenant}:TMHP adult for contracts:20270101");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task The_percentage_is_bounded(decimal percent)
    {
        (await _sut.DeployAsync(Tenant, "admin-1", Request(TmhpId, percent), default)).Error.Should().Contain("percent");
    }

    [Fact]
    public async Task Unknown_plan_or_library_schedule_is_not_found()
    {
        var plan = Request(TmhpId, 100m);
        plan.PlanId = "NOPE";
        (await _sut.DeployAsync(Tenant, "admin-1", plan, default)).IsNotFound.Should().BeTrue();
        (await _sut.DeployAsync(Tenant, "admin-1", Request("missing", 100m), default)).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task A_tenant_cannot_deploy_from_its_own_store_or_into_the_library()
    {
        (await _sut.DeployAsync(Library, "admin-1", Request(TmhpId, 100m), default)).Error.Should().Contain("library tenant");
    }

    [Fact]
    public async Task Publishing_copies_a_tenant_schedule_into_the_library_without_its_plan_link()
    {
        await _repo.UpsertAsync(new FeeSchedule
        {
            Id = "ops:plan-default:X", TenantId = "ops", Name = "Medi-Cal physician TRI", DefaultForPlanId = "X",
            Type = FeeScheduleType.Medicaid, CodeSystem = "HCPCS", EffectiveDate = new DateTime(2026, 10, 15),
            Lines = [new() { ProcedureCode = "99213", Rate = 51.00m }],
        });

        var dry = await _sut.PublishToLibraryAsync("ops", "platform-admin", new FeeSchedulePublishRequest { SourceId = "ops:plan-default:X", Jurisdiction = "ca" }, default);
        dry.Outcome.Should().Be(FeeScheduleImportOutcome.WouldWrite);

        var report = await _sut.PublishToLibraryAsync("ops", "platform-admin",
            new FeeSchedulePublishRequest { SourceId = "ops:plan-default:X", Jurisdiction = "ca", DryRun = false }, default);

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Created);
        var entry = _repo.Stored.Single(s => s.Id == $"{Library}:Medi-Cal physician TRI:20261015");
        entry.TenantId.Should().Be(Library);
        entry.DefaultForPlanId.Should().BeNull();
        entry.Jurisdiction.Should().Be("CA");
        entry.IsGlobal.Should().BeTrue();
    }

    private static PricingRequest Line(string code, string pos) => new()
    {
        TenantId = Tenant, PlanId = PlanCode, ProviderNpi = "1234567893", ProcedureCode = code,
        Modifiers = [], PlaceOfServiceCode = pos, ServiceDate = new DateTime(2027, 3, 1), BilledAmount = 500m,
    };

    private RateResolutionService Engine()
    {
        var contracts = new Mock<IProviderContractRepository>();
        contracts.Setup(c => c.GetContractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderContract?)null);
        return new RateResolutionService(_repo, contracts.Object, NullLogger<RateResolutionService>.Instance);
    }
}
