using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using CloudHealthOffice.PricingApi.Services.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ApiClaimType = CloudHealthOffice.PricingApi.Models.ClaimType;
using ApiFeeScheduleType = CloudHealthOffice.PricingApi.Models.FeeScheduleType;
using ApiRepository = CloudHealthOffice.PricingApi.Data.IFeeScheduleRepository;
using EngineFeeScheduleType = CloudHealthOffice.FeeScheduleEngine.Domain.FeeScheduleType;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// ADR 016 parity: the same contract and the same claim priced through both entry
/// points return identical allowed amounts.
///
/// <list type="bullet">
///   <item><b>Adjudication</b> — <see cref="RateResolutionService.ResolveBatchAsync"/>
///   over the canonical store (the engine's <see cref="FeeSchedule"/> and
///   <see cref="ProviderContract"/>), with the claim lines built the way
///   claims-service <c>PricingStage</c> builds them. This is what benefit-plan-service
///   <c>resolve-rates</c> runs.</item>
///   <item><b>Pricing API</b> — <see cref="RepricingService.RepriceClaimAsync"/>,
///   the self-serve repricing endpoint's service.</item>
/// </list>
///
/// Two kinds of parity are pinned:
/// <list type="number">
///   <item><b>Cross-store</b> (RBRVS, MPPR, DRG): the Pricing API reads its legacy
///   <c>fee_schedule_entries</c> rows, written by the Pricing API loader's own
///   factories; adjudication reads the same schedule in the canonical model. This is
///   also the invariant the ADR 016 backfill must keep.</item>
///   <item><b>Same store</b> (percent of Medicare, per diem, plus every scenario
///   above): both entry points read the canonical store. The legacy store cannot
///   express percent-of-Medicare or per-diem terms.</item>
/// </list>
/// The contract under test routes every code to one schedule and has no
/// lesser-of-billed provision — the contract a Pricing API caller implies.
/// </summary>
public class UnifiedPricingParityTests
{
    private const string Tenant = "tenant-parity";
    private const string ProviderNpi = "1467500001";
    private const string PlanId = "plan-parity";
    private const decimal Cf = 32.7442m;
    private static readonly DateTime ServiceDate = new(2025, 6, 2);

    // ═══════════════════════════════════════════════════════════════════
    // RBRVS
    // ═══════════════════════════════════════════════════════════════════

    private const string Rbrvs = "MEDICARE_RBRVS_2025";

    /// <summary>(code, work, PE non-facility, PE facility, MP, MPFS multiple procedure indicator)</summary>
    private static readonly (string Code, decimal Work, decimal PeNonFac, decimal PeFac, decimal Mp, int? MultProc)[] RbrvsRows =
    [
        ("99213", 1.30m, 1.59m, 0.83m, 0.09m, 0),
        ("99214", 1.92m, 2.06m, 1.12m, 0.13m, 0),
        ("27447", 19.60m, 0m, 11.65m, 3.31m, 2),
        ("29881", 7.03m, 0m, 5.38m, 1.26m, 2),
        ("20610", 0.79m, 0.89m, 0.43m, 0.10m, 2),
        ("73721", 1.09m, 5.26m, 0.80m, 0.14m, 4),
        ("71046", 0.18m, 0.67m, 0.12m, 0.04m, 0),
    ];

    public static TheoryData<string> PlacesOfService => new() { "11", "22", "21", "02", "10", "20", "49", "81" };

    [Theory]
    [MemberData(nameof(PlacesOfService))]
    public async Task Rbrvs_CrossStore_IdenticalAllowedAmounts(string pos)
    {
        var claim = new[]
        {
            Line("99213", units: 1, billed: 150m),
            Line("99214", units: 2, billed: 400m, modifiers: ["25"]),
            Line("71046", units: 1, billed: 90m, modifiers: ["26"]),
            Line("20610", units: 1, billed: 300m, modifiers: ["50"]),
        };

        var (adjudicated, repriced) = await PriceBothWays(
            claim, pos, ApiClaimType.Professional, drgCode: null,
            canonical: CanonicalRbrvs(), legacy: LegacyRbrvs());

        AssertParity(adjudicated, repriced);
        adjudicated.LineResults.Should().OnlyContain(r => r.RateSource == RateSource.MedicareMpfs);
    }

    [Fact]
    public async Task Rbrvs_ComputedFromRvus_MatchesLoaderStoredPrice()
    {
        // 99213 non-facility: (1.30 + 1.59 + 0.09) × 32.7442 = 97.58
        var (adjudicated, repriced) = await PriceBothWays(
            [Line("99213", units: 1, billed: 150m)], "11", ApiClaimType.Professional, drgCode: null,
            canonical: CanonicalRbrvs(), legacy: LegacyRbrvs());

        adjudicated.LineResults[0].AllowedAmount.Should().Be(Math.Round(2.98m * Cf, 2));
        repriced.Lines[0].AllowedAmount.Should().Be(adjudicated.LineResults[0].AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FACILITY / NON-FACILITY (CMS Pub. 100-04 Ch. 12 §20.4.2)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Urgent care (20), independent clinic (49) and independent lab (81) are
    /// non-facility settings: both entry points pay the non-facility RVU amount and
    /// the Pricing API labels the line "Non-Facility".
    /// </summary>
    [Theory]
    [InlineData("20")]
    [InlineData("49")]
    [InlineData("81")]
    public async Task NonFacilityPos_PricingApiAndAdjudicationAgree_AtNonFacilityRate(string pos)
    {
        var claim = new[] { Line("99213", units: 1, billed: 150m) };
        var nonFacility = Math.Round((1.30m + 1.59m + 0.09m) * Cf, 2);

        var (adjudicated, repriced) = await PriceBothWays(
            claim, pos, ApiClaimType.Professional, drgCode: null,
            canonical: CanonicalRbrvs(), legacy: LegacyRbrvs());
        AssertParity(adjudicated, repriced);
        adjudicated.LineResults[0].AllowedAmount.Should().Be(nonFacility);
        repriced.Lines[0].Breakdown.FacilityIndicator.Should().Be("Non-Facility");

        var (adjudicatedSame, repricedSame) = await PriceBothWaysCanonical(
            claim, pos, ApiClaimType.Professional, drgCode: null, Rbrvs, CanonicalRbrvs());
        AssertParity(adjudicatedSame, repricedSame);
        repricedSame.Lines[0].AllowedAmount.Should().Be(nonFacility);
        repricedSame.Lines[0].Breakdown.FacilityIndicator.Should().Be("Non-Facility");
    }

    /// <summary>A flat schedule with a facility price: 20/49/81 take the non-facility price on both paths.</summary>
    [Theory]
    [InlineData("20", 110.00)]
    [InlineData("49", 110.00)]
    [InlineData("81", 110.00)]
    [InlineData("22", 75.00)]
    public async Task FlatFacilityPrice_PricingApiAndAdjudicationAgree(string pos, double expected)
    {
        var schedule = new FeeSchedule
        {
            Id = "FLAT_FACILITY_2025", TenantId = Tenant, Name = "Flat with facility price",
            Type = EngineFeeScheduleType.MedicareMpfs,
            EffectiveDate = new DateTime(2025, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 110m, FacilityRate = 75m }],
        };

        var (adjudicated, repriced) = await PriceBothWaysCanonical(
            [Line("99213", units: 1, billed: 150m)], pos, ApiClaimType.Professional, drgCode: null,
            schedule.Id, schedule);

        AssertParity(adjudicated, repriced);
        repriced.Lines[0].AllowedAmount.Should().Be((decimal)expected);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MULTIPLE PROCEDURE PAYMENT REDUCTION
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(PlacesOfService))]
    public async Task Mppr_CrossStore_IdenticalRankingAndReductions(string pos)
    {
        // Three indicator-2 surgeries (ranked 100/50/50 by allowed amount), an E&M
        // (indicator 0, never reduced), an imaging line (indicator 4, unsupported:
        // unreduced and flagged), one surgery bilateral and one with modifier 51.
        var claim = new[]
        {
            Line("20610", units: 1, billed: 300m, modifiers: ["50"]),
            Line("27447", units: 1, billed: 4000m),
            Line("29881", units: 1, billed: 2000m, modifiers: ["51"]),
            Line("99214", units: 1, billed: 250m, modifiers: ["25"]),
            Line("73721", units: 1, billed: 900m),
        };

        var (adjudicated, repriced) = await PriceBothWays(
            claim, pos, ApiClaimType.Professional, drgCode: null,
            canonical: CanonicalRbrvs(), legacy: LegacyRbrvs());

        AssertParity(adjudicated, repriced);

        var reduced = adjudicated.LineResults
            .Where(r => r.Adjustments.Any(a => a.Modifier == PaymentModifiers.MultipleProcedures))
            .Select(r => r.ProcedureCode);
        reduced.Should().BeEquivalentTo("29881", "20610");
        repriced.Lines.Single(l => l.ProcedureCode == "29881").Breakdown.MultiProcReduction.Should().Be(0.50m);
        repriced.Lines.Single(l => l.ProcedureCode == "27447").Breakdown.MultiProcReduction.Should().BeNull();
        repriced.Warnings.Should().Contain(w => w.Contains("73721") && w.Contains("not yet supported"));
    }

    [Fact]
    public async Task Mppr_SameStore_IdenticalRankingAndReductions()
    {
        var claim = new[]
        {
            Line("29881", units: 1, billed: 2000m),
            Line("27447", units: 1, billed: 4000m),
            Line("20610", units: 2, billed: 600m),
        };

        var (adjudicated, repriced) = await PriceBothWaysCanonical(
            claim, "22", ApiClaimType.Professional, drgCode: null, Rbrvs, CanonicalRbrvs());

        AssertParity(adjudicated, repriced);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PERCENT OF MEDICARE
    // ═══════════════════════════════════════════════════════════════════

    private const string PercentOfMedicare = "COMMERCIAL_120_MCR_2025";

    [Theory]
    [MemberData(nameof(PlacesOfService))]
    public async Task PercentOfMedicare_SameStore_IdenticalAllowedAmounts(string pos)
    {
        var claim = new[]
        {
            Line("99213", units: 1, billed: 180m),
            Line("27447", units: 1, billed: 5000m),
            Line("29881", units: 1, billed: 2500m),
            Line("73721", units: 1, billed: 1100m, modifiers: ["26"]),
        };

        var (adjudicated, repriced) = await PriceBothWaysCanonical(
            claim, pos, ApiClaimType.Professional, drgCode: null,
            PercentOfMedicare, CanonicalRbrvs(), CanonicalPercentOfMedicare(1.20m));

        AssertParity(adjudicated, repriced);
        adjudicated.LineResults.Should().OnlyContain(r => r.RateSource == RateSource.ContractedRate);

        // 120% of the Medicare amount for the same place of service.
        var (medicare, _) = await PriceBothWaysCanonical(
            [Line("99213", units: 1, billed: 180m)], pos, ApiClaimType.Professional, drgCode: null,
            Rbrvs, CanonicalRbrvs());
        repriced.Lines[0].AllowedAmount.Should().Be(Math.Round(medicare.LineResults[0].AllowedAmount * 1.20m, 2));
    }

    // ═══════════════════════════════════════════════════════════════════
    // DRG
    // ═══════════════════════════════════════════════════════════════════

    private const string Drg = "MEDICARE_DRG_2025";
    private const decimal DrgBase = 6377.73m;

    [Fact]
    public async Task Drg_CrossStore_CaseRatePaidOnceAndAllocatedIdentically()
    {
        var claim = new[]
        {
            Line("", units: 4, billed: 12_000m, revenueCode: "0120"),
            Line("27447", units: 1, billed: 29_000m, revenueCode: "0360"),
            Line("", units: 1, billed: 1_850m, revenueCode: "0250"),
            Line("J1885", units: 3, billed: 75.50m, revenueCode: "0636"),
        };

        var (adjudicated, repriced) = await PriceBothWays(
            claim, "21", ApiClaimType.Inpatient, drgCode: "470",
            canonical: CanonicalDrg(), legacy: LegacyDrg());

        AssertParity(adjudicated, repriced);
        adjudicated.TotalAllowedAmount.Should().Be(Math.Round(DrgBase * 1.7390m, 2));
        adjudicated.LineResults.Should().OnlyContain(r => r.IsPerStayRate);
    }

    [Fact]
    public async Task Drg_SameStore_IdenticalAllowedAmounts()
    {
        var claim = new[]
        {
            Line("", units: 3, billed: 9_000m, revenueCode: "0120"),
            Line("", units: 1, billed: 3_100m, revenueCode: "0300"),
        };

        var (adjudicated, repriced) = await PriceBothWaysCanonical(
            claim, "21", ApiClaimType.Inpatient, drgCode: "291", Drg, CanonicalDrg());

        AssertParity(adjudicated, repriced);
        adjudicated.TotalAllowedAmount.Should().Be(Math.Round(DrgBase * 1.2788m, 2));
    }

    // ═══════════════════════════════════════════════════════════════════
    // PER DIEM
    // ═══════════════════════════════════════════════════════════════════

    private const string PerDiem = "BEHAVIORAL_PER_DIEM_2025";

    [Fact]
    public async Task PerDiem_SameStore_IdenticalAllowedAmounts()
    {
        // Line-level daily rates by revenue code: units are days.
        var claim = new[]
        {
            Line("", units: 5, billed: 11_000m, revenueCode: "0124"),
            Line("", units: 2, billed: 9_400m, revenueCode: "0204"),
            Line("90837", units: 3, billed: 900m, revenueCode: "0900"),
        };

        // Adjudication sends the stay's length of stay (837I admission date);
        // the Pricing API request has none. Line-level daily rates do not use it.
        var (adjudicated, repriced) = await PriceBothWaysCanonical(
            claim, "51", ApiClaimType.Inpatient, drgCode: null, PerDiem, CanonicalPerDiem(), lengthOfStay: 7);

        AssertParity(adjudicated, repriced);
        adjudicated.LineResults.Select(r => r.AllowedAmount).Should().Equal(5 * 1_450m, 2 * 2_875m, 3 * 210m);
        adjudicated.LineResults.Should().OnlyContain(r => r.RateSource == RateSource.PerDiem);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CANONICAL SCHEDULES (the engine's model, as adjudication reads it)
    // ═══════════════════════════════════════════════════════════════════

    private static FeeSchedule CanonicalRbrvs() => new()
    {
        Id = Rbrvs, TenantId = Tenant, Name = "Medicare RBRVS 2025",
        Type = EngineFeeScheduleType.MedicareMpfs,
        EffectiveDate = new DateTime(2025, 1, 1),
        ConversionFactor = Cf,
        Lines = RbrvsRows.Select(r => new FeeScheduleLine
        {
            ProcedureCode = r.Code,
            RateType = FeeScheduleRateType.Rvu,
            WorkRvu = r.Work, PeRvu = r.PeNonFac, PeRvuFacility = r.PeFac, MpRvu = r.Mp,
            MultipleProcedureIndicator = (MultipleProcedureIndicator?)(byte?)r.MultProc,
        }).ToList(),
    };

    private static FeeSchedule CanonicalPercentOfMedicare(decimal multiplier) => new()
    {
        Id = PercentOfMedicare, TenantId = Tenant, Name = "Commercial 120% of Medicare",
        Type = EngineFeeScheduleType.Commercial,
        EffectiveDate = new DateTime(2025, 1, 1),
        BaseMpfsFeeScheduleId = Rbrvs,
        Lines = RbrvsRows.Select(r => new FeeScheduleLine
        {
            ProcedureCode = r.Code,
            RateType = FeeScheduleRateType.PercentOfMedicare,
            Rate = multiplier,
            MultipleProcedureIndicator = (MultipleProcedureIndicator?)(byte?)r.MultProc,
        }).ToList(),
    };

    private static FeeSchedule CanonicalDrg() => new()
    {
        Id = Drg, TenantId = Tenant, Name = "Medicare MS-DRG FY2025",
        Type = EngineFeeScheduleType.Drg,
        EffectiveDate = new DateTime(2024, 10, 1),
        DrgBaseRate = DrgBase,
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "470", DrgWeight = 1.7390m },
            new FeeScheduleLine { ProcedureCode = "291", DrgWeight = 1.2788m },
        ],
    };

    private static FeeSchedule CanonicalPerDiem() => new()
    {
        Id = PerDiem, TenantId = Tenant, Name = "Behavioral health per diem",
        Type = EngineFeeScheduleType.PerDiem,
        EffectiveDate = new DateTime(2025, 1, 1),
        Lines =
        [
            new FeeScheduleLine { RevenueCode = "0124", Rate = 1_450m },
            new FeeScheduleLine { RevenueCode = "0204", Rate = 2_875m },
            new FeeScheduleLine { RevenueCode = "0900", Rate = 210m },
        ],
    };

    // ═══════════════════════════════════════════════════════════════════
    // LEGACY PRICING API ROWS (written by the Pricing API loader's factories)
    // ═══════════════════════════════════════════════════════════════════

    private static LegacySchedule LegacyRbrvs() => new(
        new FeeScheduleInfo
        {
            Id = Rbrvs, Name = "Medicare RBRVS 2025", Type = ApiFeeScheduleType.MedicareRbrvs, Version = "2025.1",
            EffectiveDate = new DateOnly(2025, 1, 1), CodeCount = RbrvsRows.Length, LastUpdated = DateTimeOffset.UtcNow,
        },
        RbrvsRows.Select(r => FeeScheduleLoaderService.Rbrvs(Rbrvs, r.Code, r.Code, r.Work, r.PeNonFac, r.PeFac, r.Mp, Cf, r.MultProc)).ToList());

    private static LegacySchedule LegacyDrg() => new(
        new FeeScheduleInfo
        {
            Id = Drg, Name = "Medicare MS-DRG FY2025", Type = ApiFeeScheduleType.MedicareDrg, Version = "FY2025",
            EffectiveDate = new DateOnly(2024, 10, 1), CodeCount = 2, LastUpdated = DateTimeOffset.UtcNow,
        },
        [
            FeeScheduleLoaderService.Drg(Drg, "470", "Major hip/knee joint replacement w/o MCC", 1.7390m, DrgBase),
            FeeScheduleLoaderService.Drg(Drg, "291", "Heart failure and shock w/ MCC", 1.2788m, DrgBase),
        ]);

    private sealed record LegacySchedule(FeeScheduleInfo Info, List<FeeScheduleEntry> Entries);

    // ═══════════════════════════════════════════════════════════════════
    // ENTRY POINTS
    // ═══════════════════════════════════════════════════════════════════

    private sealed record ClaimLine(string Code, decimal Units, decimal Billed, List<string>? Modifiers, string? RevenueCode);

    private static ClaimLine Line(string code, decimal units, decimal billed, List<string>? modifiers = null, string? revenueCode = null)
        => new(code, units, billed, modifiers, revenueCode);

    /// <summary>Adjudication on the canonical store; the Pricing API on its legacy rows.</summary>
    private static async Task<(PricingResultSet, RepricingResponse)> PriceBothWays(
        ClaimLine[] claim, string pos, ApiClaimType claimType, string? drgCode,
        FeeSchedule canonical, LegacySchedule legacy)
    {
        var adjudicated = await Adjudicate(claim, pos, drgCode, null, canonical.Id, canonical);

        var repo = new Mock<ApiRepository>();
        repo.Setup(r => r.GetScheduleInfoAsync(legacy.Info.Id)).ReturnsAsync(legacy.Info);
        repo.Setup(r => r.LookupCodesAsync(legacy.Info.Id, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, IEnumerable<string> codes, string? _) =>
                legacy.Entries.Where(e => codes.Contains(e.ProcedureCode, StringComparer.OrdinalIgnoreCase)).ToList());
        repo.Setup(r => r.LookupDrgAsync(legacy.Info.Id, It.IsAny<string>()))
            .ReturnsAsync((string _, string drg) => legacy.Entries.FirstOrDefault(e => e.ProcedureCode == drg));

        var service = new RepricingService(repo.Object, NullLogger<RepricingService>.Instance);
        var repriced = await service.RepriceClaimAsync(RepricingRequestFor(claim, pos, claimType, drgCode, legacy.Info.Id));
        return (adjudicated, repriced);
    }

    /// <summary>Both entry points on the canonical store.</summary>
    private static async Task<(PricingResultSet, RepricingResponse)> PriceBothWaysCanonical(
        ClaimLine[] claim, string pos, ApiClaimType claimType, string? drgCode,
        string scheduleId, params FeeSchedule[] schedules)
        => await PriceBothWaysCanonical(claim, pos, claimType, drgCode, scheduleId, schedules, lengthOfStay: null);

    private static async Task<(PricingResultSet, RepricingResponse)> PriceBothWaysCanonical(
        ClaimLine[] claim, string pos, ApiClaimType claimType, string? drgCode,
        string scheduleId, FeeSchedule schedule, int? lengthOfStay)
        => await PriceBothWaysCanonical(claim, pos, claimType, drgCode, scheduleId, [schedule], lengthOfStay);

    private static async Task<(PricingResultSet, RepricingResponse)> PriceBothWaysCanonical(
        ClaimLine[] claim, string pos, ApiClaimType claimType, string? drgCode,
        string scheduleId, FeeSchedule[] schedules, int? lengthOfStay)
    {
        var adjudicated = await Adjudicate(claim, pos, drgCode, lengthOfStay, scheduleId, schedules);

        var store = new CanonicalStore(schedules, contractScheduleId: scheduleId);
        var service = new RepricingService(
            new Mock<ApiRepository>(MockBehavior.Strict).Object,
            new EngineStoreScheduleSource(store, Tenant),
            NullLogger<RepricingService>.Instance,
            NullLoggerFactory.Instance);
        var repriced = await service.RepriceClaimAsync(RepricingRequestFor(claim, pos, claimType, drgCode, scheduleId));
        return (adjudicated, repriced);
    }

    /// <summary>
    /// The adjudication path: the provider's contract in the canonical store and the
    /// claim's lines as claims-service <c>PricingStage.BuildRequests</c> builds them,
    /// priced in one batch as benefit-plan-service <c>resolve-rates</c> does.
    /// </summary>
    private static Task<PricingResultSet> Adjudicate(
        ClaimLine[] claim, string pos, string? drgCode, int? lengthOfStay,
        string contractScheduleId, params FeeSchedule[] schedules)
    {
        var store = new CanonicalStore(schedules, contractScheduleId);
        var engine = new RateResolutionService(store, store, NullLogger<RateResolutionService>.Instance);

        var requests = claim.Select((line, i) => new PricingRequest
        {
            TenantId = Tenant,
            ProcedureCode = line.Code,
            Modifiers = line.Modifiers ?? [],
            ProviderNpi = ProviderNpi,
            PlanId = PlanId,
            PlaceOfServiceCode = pos,
            ServiceDate = ServiceDate,
            BilledAmount = line.Billed,
            Units = line.Units,
            LineNumber = i + 1,
            TotalLineCount = claim.Length,
            DrgCode = drgCode,
            LengthOfStay = lengthOfStay,
            RevenueCode = line.RevenueCode,
        }).ToList();

        return engine.ResolveBatchAsync(requests);
    }

    private static RepricingRequest RepricingRequestFor(
        ClaimLine[] claim, string pos, ApiClaimType claimType, string? drgCode, string scheduleId) => new()
    {
        FeeScheduleId = scheduleId,
        ClaimType = claimType,
        PlaceOfService = pos,
        DrgCode = drgCode,
        Lines = claim.Select((line, i) => new ClaimLineRequest
        {
            LineNumber = i + 1,
            ProcedureCode = line.Code,
            Modifiers = line.Modifiers,
            RevenueCode = line.RevenueCode,
            Units = line.Units,
            BilledAmount = line.Billed,
            ServiceDate = DateOnly.FromDateTime(ServiceDate),
        }).ToList(),
    };

    private static void AssertParity(PricingResultSet adjudicated, RepricingResponse repriced)
    {
        repriced.Lines.Should().HaveCount(adjudicated.LineResults.Count);
        repriced.Lines.Should().OnlyContain(l => l.Status == PricingStatus.Priced);
        adjudicated.LineResults.Should().OnlyContain(r => r.RateSource != RateSource.BilledCharges && r.RateSource != RateSource.Unresolved);

        repriced.Lines.Select(l => l.AllowedAmount)
            .Should().Equal(adjudicated.LineResults.OrderBy(r => r.LineNumber).Select(r => r.AllowedAmount));
        repriced.TotalAllowed.Should().Be(adjudicated.TotalAllowedAmount);
        adjudicated.TotalAllowedAmount.Should().BeGreaterThan(0m);
    }

    /// <summary>The canonical store: fee schedules plus the provider's contract (no carve-outs, no lesser-of).</summary>
    private sealed class CanonicalStore : IFeeScheduleRepository, IProviderContractRepository
    {
        private readonly Dictionary<string, FeeSchedule> _schedules;
        private readonly string _contractScheduleId;

        public CanonicalStore(IEnumerable<FeeSchedule> schedules, string contractScheduleId)
        {
            _schedules = schedules.ToDictionary(s => s.Id);
            _contractScheduleId = contractScheduleId;
        }

        public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
            => Task.FromResult(tenantId == Tenant ? _schedules.GetValueOrDefault(id) : null);

        public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(null);

        public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
            => Task.FromResult<FeeScheduleLine?>(null);

        public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>(_schedules.Values.ToList());

        public Task<ProviderContract?> GetContractAsync(string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<ProviderContract?>(new ProviderContract
            {
                Id = ProviderContract.MakeId(tenantId, providerNpi, planId),
                TenantId = tenantId,
                ProviderNpi = providerNpi,
                PlanId = planId,
                NetworkStatus = NetworkStatus.InNetwork,
                FeeScheduleId = _contractScheduleId,
                EffectiveDate = new DateTime(2025, 1, 1),
            });

        public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderContract>>([]);
    }
}
