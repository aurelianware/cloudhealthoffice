using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.GoldenPath.Tests.Harness;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// The stored configuration every scenario shares. All data is synthetic.
///
/// <para><b>Plan</b> (<c>Fixtures/plan-golden-ppo-2026.json</c>, a stored
/// benefit-plan-service document read through <c>ChoBenefitPlanProvider</c>):
/// PPO, Embedded family model, in-network individual deductible $500 / OOP max
/// $3,000 (family $1,500 / $6,000).
/// Office Visit and Surgery: deductible, then 20% coinsurance.
/// Acupuncture: not covered.
/// Inpatient Hospital: deductible, $250 copay (after the deductible), then 10%.</para>
///
/// <para><b>Contracts and fee schedules</b> (benefit-plan-service fee schedule engine):
/// <list type="bullet">
///   <item>NPI 1999999976 (Dr. Dana, family clinic) → commercial schedule:
///     99213 $100, 99214 $150, 97810 $60 (E&amp;M / not-surgical, MPI 0);
///     29881 $1,200, 11042 $200, 20610 $80 (MPI 2: multiple-procedure ranking).</item>
///   <item>NPI 1999999950 (Dr. Jordan, specialty group) → 125% of Medicare:
///     99214 percent-of-Medicare 1.25 against the synthetic MPFS schedule
///     (CF $33.0000, GPCIs 1.0; 99214 work 1.92 + PE 2.06 + MP 0.13 RVU).</item>
///   <item>NPI 1999999984 (general hospital) → DRG schedule, base rate $6,000;
///     DRG 470 weight 3.0000 → $18,000 case rate.</item>
/// </list></para>
///
/// <para><b>Service category mappings</b> (tenant level, as an operator authors
/// them; the REV rule has the same shape as the shipped
/// <c>schemas/service-category-mappings/system-defaults.json</c>):
/// CPT 99202–99215 → Office Visit; CPT 10000–69999 → Surgery;
/// CPT 97810–97814 → Acupuncture; REV 0100–0219 → Inpatient Hospital.</para>
/// </summary>
internal static class GoldenInputs
{
    public const string ClinicNpi = "1999999976";
    public const string SpecialistNpi = "1999999950";
    public const string HospitalNpi = "1999999984";

    private const string Tenant = GoldenScenario.TenantId;
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string PlanDocument =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "plan-golden-ppo-2026.json"));

    public static string PlanId => "6f1c2b0e-3d4a-4b5c-8e9f-0a1b2c3d4e5f";

    public static string Edi837(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "837", name + ".837"));

    public static IReadOnlyList<FeeSchedule> FeeSchedules =>
    [
        new FeeSchedule
        {
            Id = "golden:commercial-2026", TenantId = Tenant, Name = "Golden commercial 2026",
            Type = FeeScheduleType.Commercial, EffectiveDate = Effective,
            Lines =
            [
                Flat("99213", 100.00m, MultipleProcedureIndicator.NoReduction),
                Flat("99214", 150.00m, MultipleProcedureIndicator.NoReduction),
                Flat("97810", 60.00m, MultipleProcedureIndicator.NoReduction),
                Flat("29881", 1200.00m, MultipleProcedureIndicator.StandardSurgery),
                Flat("11042", 200.00m, MultipleProcedureIndicator.StandardSurgery),
                Flat("20610", 80.00m, MultipleProcedureIndicator.StandardSurgery),
            ],
        },
        new FeeSchedule
        {
            Id = "golden:mpfs-2026", TenantId = Tenant, Name = "Golden synthetic MPFS 2026",
            Type = FeeScheduleType.MedicareMpfs, EffectiveDate = Effective,
            ConversionFactor = 33.0000m,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99214", RateType = FeeScheduleRateType.Rvu,
                    WorkRvu = 1.92m, PeRvu = 2.06m, PeRvuFacility = 0.83m, MpRvu = 0.13m,
                    MultipleProcedureIndicator = MultipleProcedureIndicator.NoReduction,
                },
            ],
        },
        new FeeSchedule
        {
            Id = "golden:pct-medicare-2026", TenantId = Tenant, Name = "Golden 125% of Medicare 2026",
            Type = FeeScheduleType.Commercial, EffectiveDate = Effective,
            BaseMpfsFeeScheduleId = "golden:mpfs-2026",
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99214", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.25m,
                    MultipleProcedureIndicator = MultipleProcedureIndicator.NoReduction,
                },
            ],
        },
        new FeeSchedule
        {
            Id = "golden:drg-2026", TenantId = Tenant, Name = "Golden DRG 2026",
            Type = FeeScheduleType.Drg, EffectiveDate = Effective,
            DrgBaseRate = 6000.00m,
            Lines = [new FeeScheduleLine { ProcedureCode = "470", RateType = FeeScheduleRateType.FlatRate, DrgWeight = 3.0000m }],
        },
    ];

    public static IReadOnlyList<ProviderContract> Contracts =>
    [
        Contract(ClinicNpi, "golden:commercial-2026"),
        Contract(SpecialistNpi, "golden:pct-medicare-2026"),
        Contract(HospitalNpi, "golden:drg-2026"),
    ];

    public static IReadOnlyList<ServiceCategoryMapping> CategoryMappings =>
    [
        Mapping("Office Visit", "CPT", "99202", "99215"),
        Mapping("Surgery", "CPT", "10000", "69999"),
        Mapping("Acupuncture", "CPT", "97810", "97814"),
        Mapping("Inpatient Hospital", "REV", "0100", "0219"),
    ];

    public static GoldenScenario Scenario(Action<InMemoryAccumulatorService>? prior = null) => new()
    {
        PlanDocument = PlanDocument,
        FeeSchedules = FeeSchedules,
        Contracts = Contracts,
        CategoryMappings = CategoryMappings,
        PriorAccumulators = prior,
    };

    /// <summary>Prior in-network activity this plan year (individual and family, single-member family).</summary>
    public static Action<InMemoryAccumulatorService> Prior(decimal deductible, decimal oop) => acc =>
    {
        acc.Seed(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual, deductible);
        acc.Seed(AccumulatorType.FamilyDeductible, AccumulatorScope.Family, deductible);
        acc.Seed(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual, oop);
        acc.Seed(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family, oop);
    };

    private static FeeScheduleLine Flat(string code, decimal rate, MultipleProcedureIndicator mpi) => new()
    {
        ProcedureCode = code, RateType = FeeScheduleRateType.FlatRate, Rate = rate, MultipleProcedureIndicator = mpi,
    };

    private static ProviderContract Contract(string npi, string scheduleId) => new()
    {
        Id = ProviderContract.MakeId(Tenant, npi, PlanId), TenantId = Tenant, ProviderNpi = npi, PlanId = PlanId,
        EffectiveDate = Effective, NetworkStatus = NetworkStatus.InNetwork, FeeScheduleId = scheduleId,
    };

    private static ServiceCategoryMapping Mapping(string category, string codeType, string from, string to) => new()
    {
        Id = Guid.NewGuid(), TenantId = Tenant, BenefitPlanId = null,
        ServiceTypeCode = category, ServiceTypeDescription = category, IsActive = true,
        Rules = [new ProcedureCodeRule { Priority = 10, CodeType = codeType, CodePattern = from, CodeRangeEnd = to }],
    };
}
