using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

public class RateResolutionServiceTests
{
    private const string Tenant = "test-tenant";
    private const string PlanId = "plan-001";
    private const string ProviderNpi = "1234567890";

    // ═══════════════════════════════════════════════════════════════════
    // MEDICARE MPFS / RVU CALCULATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard MPFS RVU calculation:
    /// (WorkRVU × WorkGPCI + PeRVU × PeGPCI + MpRVU × MpGPCI) × CF
    /// 99213: Work=1.30, PE(non-fac)=1.59, MP=0.09, CF=33.8872
    /// Non-facility (POS 11): (1.30×1.0 + 1.59×1.0 + 0.09×1.0) × 33.8872 = 100.98
    /// </summary>
    [Fact]
    public async Task MedicareMpfs_NonFacility_CalculatesRvuCorrectly()
    {
        var schedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m);
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11"));

        Assert.Equal(FeeScheduleType.MedicareMpfs, result.FeeScheduleType);
        Assert.Equal(RateSource.MedicareMpfs, result.RateSource);
        // (1.30 + 1.59 + 0.09) × 33.8872 = 100.98
        Assert.Equal(100.98m, result.AllowedAmount);
    }

    /// <summary>
    /// Facility POS (21 = inpatient hospital) uses facility PE RVU.
    /// 99213 facility: (1.30×1.0 + 0.83×1.0 + 0.09×1.0) × 33.8872 = 75.23
    /// </summary>
    [Fact]
    public async Task MedicareMpfs_Facility_UsesFacilityPeRvu()
    {
        var schedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m);
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "21"));

        // (1.30 + 0.83 + 0.09) × 33.8872 = 75.23
        Assert.Equal(75.23m, result.AllowedAmount);
    }

    /// <summary>
    /// GPCI locality adjustment.
    /// Locality 01 (Manhattan): Work=1.058, PE=1.281, MP=1.475
    /// 99213: (1.30×1.058 + 1.59×1.281 + 0.09×1.475) × 33.8872 = 116.80
    /// </summary>
    [Fact]
    public async Task MedicareMpfs_WithGpci_AppliesLocalityAdjustment()
    {
        var schedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m,
            workGpci: 1.058m, peGpci: 1.281m, mpGpci: 1.475m);
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11"));

        // (1.30×1.058 + 1.59×1.281 + 0.09×1.475) × 33.8872
        var expected = Math.Round((1.30m * 1.058m + 1.59m * 1.281m + 0.09m * 1.475m) * 33.8872m, 2);
        Assert.Equal(expected, result.AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MEDICAID — PERCENT OF MEDICARE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Medicaid flat rate — no percent-of-Medicare, just a stored rate.
    /// </summary>
    [Fact]
    public async Task Medicaid_FlatRate_ReturnsStoredRate()
    {
        var schedule = new FeeSchedule
        {
            Id = "medicaid-flat", TenantId = Tenant, Name = "AZ Medicaid 2026",
            Type = FeeScheduleType.Medicaid,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 45.00m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213"));

        Assert.Equal(45.00m, result.AllowedAmount);
        Assert.Equal(RateSource.Medicaid, result.RateSource);
    }

    /// <summary>
    /// Medicaid percent-of-Medicare with cross-schedule lookup.
    /// Base MPFS rate for 99213 = 100.98 (from RVU calc above).
    /// Medicaid = 72% of Medicare = 72.71
    /// </summary>
    [Fact]
    public async Task Medicaid_PercentOfMedicare_CrossScheduleLookup()
    {
        var mpfsSchedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m);
        mpfsSchedule.Id = "mpfs-2026";

        var medicaidSchedule = new FeeSchedule
        {
            Id = "medicaid-pctmed", TenantId = Tenant, Name = "AZ Medicaid 72% of Medicare",
            Type = FeeScheduleType.Medicaid,
            EffectiveDate = new DateTime(2026, 1, 1),
            PercentOfMedicare = 0.72m,
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 0m }]
        };

        var repo = new InMemoryFeeScheduleRepo(medicaidSchedule);
        repo.AddSchedule(mpfsSchedule);
        var engine = CreateEngine(medicaidSchedule, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11"));

        // 100.98 × 0.72 = 72.71
        Assert.Equal(72.71m, result.AllowedAmount);
        Assert.Equal(RateSource.Medicaid, result.RateSource);
    }

    /// <summary>
    /// Medicaid cross-schedule lookup where the MPFS reference has an RVU line but
    /// no conversion factor: the stored Rate is not a usable Medicare rate, so the
    /// line is unresolved rather than priced off it.
    /// </summary>
    [Fact]
    public async Task Medicaid_RvuReferenceWithoutConversionFactor_Unresolved()
    {
        var mpfsSchedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m);
        mpfsSchedule.Id = "mpfs-2026";
        mpfsSchedule.ConversionFactor = null;

        var medicaidSchedule = new FeeSchedule
        {
            Id = "medicaid-nocf", TenantId = Tenant, Name = "AZ Medicaid 72% of Medicare",
            Type = FeeScheduleType.Medicaid,
            EffectiveDate = new DateTime(2026, 1, 1),
            PercentOfMedicare = 0.72m,
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 0m }]
        };

        var repo = new InMemoryFeeScheduleRepo(medicaidSchedule);
        repo.AddSchedule(mpfsSchedule);
        var engine = CreateEngine(medicaidSchedule, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11", billed: 500m));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Equal(FeeScheduleType.Medicaid, result.FeeScheduleType);
        Assert.Equal(0m, result.AllowedAmount);
        Assert.Contains("mpfs-2026", result.UnresolvedReason);
    }

    /// <summary>
    /// Medicaid with inline RVU values and percent-of-Medicare.
    /// The Medicaid schedule stores its own RVU values and applies percent.
    /// </summary>
    [Fact]
    public async Task Medicaid_InlineRvu_WithPercent()
    {
        var schedule = new FeeSchedule
        {
            Id = "medicaid-rvu", TenantId = Tenant, Name = "Medicaid Inline RVU",
            Type = FeeScheduleType.Medicaid,
            EffectiveDate = new DateTime(2026, 1, 1),
            ConversionFactor = 33.8872m,
            PercentOfMedicare = 0.72m,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99213",
                    RateType = FeeScheduleRateType.Rvu,
                    WorkRvu = 1.30m, PeRvu = 1.59m, MpRvu = 0.09m
                }
            ]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11"));

        // RVU = (1.30 + 1.59 + 0.09) × 33.8872 = 100.98 × 0.72 = 72.71
        Assert.Equal(72.71m, result.AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DRG CASE RATE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// DRG flat case rate — ProcedureCode field on the line holds the DRG code.
    /// </summary>
    [Fact]
    public async Task Drg_FlatCaseRate_LookupByDrgCode()
    {
        var schedule = new FeeSchedule
        {
            Id = "drg-2026", TenantId = Tenant, Name = "DRG Case Rates 2026",
            Type = FeeScheduleType.Drg,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "470", Rate = 12000m },
                new FeeScheduleLine { ProcedureCode = "871", Rate = 8500m },
            ]
        };
        var engine = CreateEngine(schedule);

        var request = CreateRequest("99223", drgCode: "470");
        var result = await engine.ResolveAsync(request);

        Assert.Equal(12000m, result.AllowedAmount);
        Assert.Equal(RateSource.Drg, result.RateSource);
        Assert.Equal(FeeScheduleType.Drg, result.FeeScheduleType);
    }

    /// <summary>
    /// DRG weight-based: base rate × DRG weight.
    /// Base rate = $5,000, DRG 470 weight = 2.4 → $12,000
    /// </summary>
    [Fact]
    public async Task Drg_WeightBased_CalculatesFromBaseRate()
    {
        var schedule = new FeeSchedule
        {
            Id = "drg-weight", TenantId = Tenant, Name = "DRG Weight-Based",
            Type = FeeScheduleType.Drg,
            EffectiveDate = new DateTime(2026, 1, 1),
            DrgBaseRate = 5000m,
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "470", Rate = 0m, DrgWeight = 2.4m },
                new FeeScheduleLine { ProcedureCode = "871", Rate = 0m, DrgWeight = 1.7m },
            ]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99223", drgCode: "470"));

        Assert.Equal(12000m, result.AllowedAmount);
    }

    /// <summary>
    /// DRG rates should NOT apply modifier adjustments or unit multipliers.
    /// </summary>
    [Fact]
    public async Task Drg_NoModifierAdjustments()
    {
        var schedule = new FeeSchedule
        {
            Id = "drg-nomod", TenantId = Tenant, Name = "DRG No Modifiers",
            Type = FeeScheduleType.Drg,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "470", Rate = 12000m }]
        };
        var engine = CreateEngine(schedule);

        var request = CreateRequest("99223", drgCode: "470", modifiers: ["22", "50"]);
        var result = await engine.ResolveAsync(request);

        // Should be flat 12000 — no 22 (125%) or 50 (150%) adjustments
        Assert.Equal(12000m, result.AllowedAmount);
        Assert.Empty(result.Adjustments);
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMMERCIAL FLAT RATE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Commercial_FlatRate_ReturnsContractedRate()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-2026", TenantId = Tenant, Name = "Commercial Contracted",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 125.00m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213"));

        Assert.Equal(125.00m, result.AllowedAmount);
        Assert.Equal(RateSource.ContractedRate, result.RateSource);
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMMERCIAL PERCENT OF MEDICARE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 110% of Medicare via the referenced MPFS schedule.
    /// Medicare 99213 = 100.98 → 100.98 × 1.10 = 111.08 (not 110% of billed).
    /// </summary>
    [Fact]
    public async Task Commercial_PercentOfMedicare_AppliesToMedicareRateNotBilled()
    {
        var mpfsSchedule = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m,
            cf: 33.8872m);
        mpfsSchedule.Id = "mpfs-2026";

        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed", TenantId = Tenant, Name = "Commercial 110% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m }]
        };

        var repo = new InMemoryFeeScheduleRepo(commercial);
        repo.AddSchedule(mpfsSchedule);
        var engine = CreateEngine(commercial, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11", billed: 500m));

        Assert.Equal(111.08m, result.AllowedAmount);
        Assert.Equal(RateSource.ContractedRate, result.RateSource);
        Assert.Null(result.UnresolvedReason);
    }

    /// <summary>
    /// Percent-of-Medicare with RVUs stored inline on the commercial line
    /// and the schedule's own GPCI/CF: 100.98 × 1.20 = 121.18.
    /// </summary>
    [Fact]
    public async Task Commercial_PercentOfMedicare_InlineRvu()
    {
        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-rvu", TenantId = Tenant, Name = "Commercial 120% of Medicare (RVU)",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            ConversionFactor = 33.8872m,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.20m,
                    WorkRvu = 1.30m, PeRvu = 1.59m, PeRvuFacility = 0.83m, MpRvu = 0.09m
                }
            ]
        };
        var engine = CreateEngine(commercial);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11", billed: 500m));

        Assert.Equal(121.18m, result.AllowedAmount);
        Assert.Equal(RateSource.ContractedRate, result.RateSource);
    }

    /// <summary>
    /// Percent-of-Medicare with no Medicare reference schedule or RVUs must not
    /// fall back to a percentage of billed charges — the line is unresolved.
    /// </summary>
    [Fact]
    public async Task Commercial_PercentOfMedicare_NoMedicareReference_UnresolvedNotBilled()
    {
        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-noref", TenantId = Tenant, Name = "Commercial 110% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m }]
        };
        var engine = CreateEngine(commercial);

        var result = await engine.ResolveAsync(CreateRequest("99213", billed: 500m));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Equal(0m, result.AllowedAmount);
        Assert.NotEqual(550m, result.AllowedAmount); // the old 110%-of-billed result
        Assert.False(string.IsNullOrWhiteSpace(result.UnresolvedReason));
        Assert.Equal("comm-pctmed-noref", result.FeeScheduleId);
        Assert.Empty(result.Adjustments);
    }

    /// <summary>
    /// Referenced Medicare schedule missing, or missing the code → unresolved.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commercial_PercentOfMedicare_ReferenceUnusable_Unresolved(bool referenceExists)
    {
        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-badref", TenantId = Tenant, Name = "Commercial 110% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m }]
        };
        var repo = new InMemoryFeeScheduleRepo(commercial);
        if (referenceExists)
        {
            // Reference schedule exists but has no line for 99213
            var mpfs = CreateMpfsSchedule("99214",
                workRvu: 1.92m, peRvu: 2.07m, peRvuFacility: 1.0m, mpRvu: 0.13m, cf: 33.8872m);
            mpfs.Id = "mpfs-2026";
            repo.AddSchedule(mpfs);
        }
        var engine = CreateEngine(commercial, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", billed: 500m));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Equal(0m, result.AllowedAmount);
        Assert.Contains("mpfs-2026", result.UnresolvedReason);
    }

    /// <summary>
    /// Reference schedule missing / lacking the code, but the line carries inline
    /// RVUs and the schedule has a CF → falls through to the inline RVUs.
    /// 100.98 × 1.20 = 121.18.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commercial_PercentOfMedicare_ReferenceUnusable_FallsBackToInlineRvu(bool referenceExists)
    {
        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-fallback", TenantId = Tenant, Name = "Commercial 120% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            BaseMpfsFeeScheduleId = "mpfs-2026",
            ConversionFactor = 33.8872m,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.20m,
                    WorkRvu = 1.30m, PeRvu = 1.59m, PeRvuFacility = 0.83m, MpRvu = 0.09m
                }
            ]
        };
        var repo = new InMemoryFeeScheduleRepo(commercial);
        if (referenceExists)
        {
            var mpfs = CreateMpfsSchedule("99214",
                workRvu: 1.92m, peRvu: 2.07m, peRvuFacility: 1.0m, mpRvu: 0.13m, cf: 33.8872m);
            mpfs.Id = "mpfs-2026";
            repo.AddSchedule(mpfs);
        }
        var engine = CreateEngine(commercial, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11", billed: 500m));

        Assert.Equal(RateSource.ContractedRate, result.RateSource);
        Assert.Equal(121.18m, result.AllowedAmount);
    }

    /// <summary>
    /// An RVU reference line on a schedule with no conversion factor is not a
    /// usable Medicare rate (its stored Rate is not maintained) → unresolved,
    /// not priced off the stale/zero Rate.
    /// </summary>
    [Fact]
    public async Task Commercial_PercentOfMedicare_RvuReferenceWithoutConversionFactor_Unresolved()
    {
        var mpfs = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m, cf: 33.8872m);
        mpfs.Id = "mpfs-2026";
        mpfs.ConversionFactor = null;

        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-nocf", TenantId = Tenant, Name = "Commercial 110% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m }]
        };
        var repo = new InMemoryFeeScheduleRepo(commercial);
        repo.AddSchedule(mpfs);
        var engine = CreateEngine(commercial, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", billed: 500m));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Equal(0m, result.AllowedAmount);
        Assert.Contains("mpfs-2026", result.UnresolvedReason);
    }

    // ═══════════════════════════════════════════════════════════════════
    // UNITS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// BilledAmount is the line total, so 80% of a $300 3-unit line is $240,
    /// not $240 × 3.
    /// </summary>
    [Fact]
    public async Task Units_PercentOfBilled_NotMultipliedByUnits()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-pctbilled", TenantId = Tenant, Name = "Commercial 80% of Billed",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "97110", RateType = FeeScheduleRateType.PercentOfBilled, Rate = 0.80m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("97110", billed: 300m, units: 3));

        Assert.Equal(240m, result.AllowedAmount);
    }

    /// <summary>Billed-charges fallback is also the line total — not multiplied by units.</summary>
    [Fact]
    public async Task Units_BilledChargesFallback_NotMultipliedByUnits()
    {
        var engine = CreateEngine(schedule: null);

        var result = await engine.ResolveAsync(CreateRequest("97110", billed: 300m, units: 3));

        Assert.Equal(RateSource.BilledCharges, result.RateSource);
        Assert.Equal(300m, result.AllowedAmount);
    }

    /// <summary>Flat per-unit rates are still multiplied by units: $50 × 3 = $150.</summary>
    [Fact]
    public async Task Units_FlatRate_MultipliedByUnits()
    {
        var engine = CreateEngine(CreateCommercialSchedule("97110", 50m));

        var result = await engine.ResolveAsync(CreateRequest("97110", billed: 300m, units: 3));

        Assert.Equal(150m, result.AllowedAmount);
    }

    /// <summary>
    /// Percent-of-Medicare is a per-unit Medicare rate × %, so it is multiplied
    /// by units: 100.98 × 1.10 = 111.08 per unit × 2 = 222.16.
    /// </summary>
    [Fact]
    public async Task Units_PercentOfMedicare_MultipliedByUnits()
    {
        var mpfs = CreateMpfsSchedule("99213",
            workRvu: 1.30m, peRvu: 1.59m, peRvuFacility: 0.83m, mpRvu: 0.09m, cf: 33.8872m);
        mpfs.Id = "mpfs-2026";
        var commercial = new FeeSchedule
        {
            Id = "comm-pctmed-units", TenantId = Tenant, Name = "Commercial 110% of Medicare",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            BaseMpfsFeeScheduleId = "mpfs-2026",
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m }]
        };
        var repo = new InMemoryFeeScheduleRepo(commercial);
        repo.AddSchedule(mpfs);
        var engine = CreateEngine(commercial, repo: repo);

        var result = await engine.ResolveAsync(CreateRequest("99213", pos: "11", billed: 500m, units: 2));

        Assert.Equal(222.16m, result.AllowedAmount);
    }

    /// <summary>
    /// Multi-unit percent-of-billed line in a batch: the rank reduction applies
    /// to the line total once (80% × $300 = $240 → 50% = $120).
    /// </summary>
    [Fact]
    public async Task Units_PercentOfBilled_InBatchReduction_UsesLineTotal()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-pctbilled-batch", TenantId = Tenant, Name = "Commercial Mixed",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", Rate = 1500m },
                new FeeScheduleLine { ProcedureCode = "20610", RateType = FeeScheduleRateType.PercentOfBilled, Rate = 0.80m },
            ]
        };
        var engine = CreateEngine(schedule);

        var resultSet = await engine.ResolveBatchAsync(
        [
            CreateRequest("27447", lineNumber: 1, totalLines: 2),
            CreateRequest("20610", lineNumber: 2, totalLines: 2, billed: 300m, units: 3),
        ]);

        Assert.Equal(1500m, resultSet.LineResults.Single(r => r.LineNumber == 1).AllowedAmount);
        Assert.Equal(120m, resultSet.LineResults.Single(r => r.LineNumber == 2).AllowedAmount);
    }

    /// <summary>
    /// An unresolved line takes no multiple procedure rank and is not reduced;
    /// the remaining eligible lines rank among themselves.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_UnresolvedLine_ExcludedFromRanking()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-mixed", TenantId = Tenant, Name = "Commercial Mixed",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", RateType = FeeScheduleRateType.PercentOfMedicare, Rate = 1.10m },
                new FeeScheduleLine { ProcedureCode = "29881", Rate = 800m },
                new FeeScheduleLine { ProcedureCode = "20610", Rate = 200m },
            ]
        };
        var engine = CreateEngine(schedule);

        var resultSet = await engine.ResolveBatchAsync(
        [
            CreateRequest("27447", lineNumber: 1, totalLines: 3, billed: 5000m),
            CreateRequest("29881", lineNumber: 2, totalLines: 3),
            CreateRequest("20610", lineNumber: 3, totalLines: 3),
        ]);

        var unresolved = resultSet.LineResults.Single(r => r.LineNumber == 1);
        Assert.Equal(RateSource.Unresolved, unresolved.RateSource);
        Assert.Equal(0m, unresolved.AllowedAmount);
        Assert.Empty(unresolved.Adjustments);

        Assert.Equal(800m, resultSet.LineResults.Single(r => r.LineNumber == 2).AllowedAmount);
        Assert.Equal(100m, resultSet.LineResults.Single(r => r.LineNumber == 3).AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // UCR FALLBACK
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoScheduleFound_FallsBackToBilledCharges()
    {
        var engine = CreateEngine(schedule: null);

        var result = await engine.ResolveAsync(CreateRequest("99999", billed: 500m));

        Assert.Equal(500m, result.AllowedAmount);
        Assert.Equal(RateSource.BilledCharges, result.RateSource);
        Assert.Equal(FeeScheduleType.Ucr, result.FeeScheduleType);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIER ADJUSTMENTS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Modifier50_Bilateral_150Percent()
    {
        var schedule = CreateCommercialSchedule("27447", 1500m);
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("27447", modifiers: ["50"]));

        Assert.Equal(2250m, result.AllowedAmount); // 1500 × 1.5
        Assert.Contains(result.Adjustments, a => a.Modifier == "50");
    }

    [Fact]
    public async Task Modifier52_ReducedServices_50Percent()
    {
        var schedule = CreateCommercialSchedule("27447", 1500m);
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("27447", modifiers: ["52"]));

        Assert.Equal(750m, result.AllowedAmount); // 1500 × 0.50
    }

    // ═══════════════════════════════════════════════════════════════════
    // MULTIPLE PROCEDURE RANKING (ResolveBatchAsync)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Batch with 3 eligible lines — CMS multiple surgery (indicator 2):
    /// highest-paid at 100%, second and third at 50%.
    /// Lines are ranked by allowed amount, not by line number.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_ThreeEligibleSurgeries_100_50_50()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-multi", TenantId = Tenant, Name = "Commercial Multi",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", Rate = 1500m }, // Highest
                new FeeScheduleLine { ProcedureCode = "29881", Rate = 800m },  // Second
                new FeeScheduleLine { ProcedureCode = "20610", Rate = 200m },  // Third
            ]
        };
        var engine = CreateEngine(schedule);

        var requests = new List<PricingRequest>
        {
            CreateRequest("20610", lineNumber: 1, totalLines: 3, billed: 300m),  // Lowest rate, line 1
            CreateRequest("27447", lineNumber: 2, totalLines: 3, billed: 2000m), // Highest rate, line 2
            CreateRequest("29881", lineNumber: 3, totalLines: 3, billed: 1000m), // Mid rate, line 3
        };

        var resultSet = await engine.ResolveBatchAsync(requests);

        // 27447 (highest=$1500) should be at 100%
        var line2 = resultSet.LineResults.First(r => r.ProcedureCode == "27447");
        Assert.Equal(1500m, line2.AllowedAmount);

        // 29881 (second=$800) should be at 50% = $400
        var line3 = resultSet.LineResults.First(r => r.ProcedureCode == "29881");
        Assert.Equal(400m, line3.AllowedAmount);

        // 20610 (third=$200) should also be at 50% = $100 (2nd–5th all 50%)
        var line1 = resultSet.LineResults.First(r => r.ProcedureCode == "20610");
        Assert.Equal(100m, line1.AllowedAmount);
        Assert.Contains(line1.Adjustments, a => a.Modifier == "51" && a.AdjustmentFactor == 0.50m);
    }

    /// <summary>
    /// E&amp;M lines are not subject to multiple procedure reduction
    /// (rate line flag off) — they neither take a rank nor get reduced, even
    /// when they are the highest-paid line on the claim.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_EmPlusTwoSurgeries_EmUnreduced()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-em", TenantId = Tenant, Name = "Commercial E&M",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "99215", Rate = 900m, MultipleProcedureReductionApplies = false },
                new FeeScheduleLine { ProcedureCode = "29881", Rate = 800m },
                new FeeScheduleLine { ProcedureCode = "20610", Rate = 200m },
            ]
        };
        var engine = CreateEngine(schedule);

        var requests = new List<PricingRequest>
        {
            CreateRequest("99215", lineNumber: 1, totalLines: 3, billed: 1000m, modifiers: ["25"]),
            CreateRequest("29881", lineNumber: 2, totalLines: 3, billed: 1000m),
            CreateRequest("20610", lineNumber: 3, totalLines: 3, billed: 300m),
        };

        var resultSet = await engine.ResolveBatchAsync(requests);

        var em = resultSet.LineResults.Single(r => r.ProcedureCode == "99215");
        Assert.Equal(900m, em.AllowedAmount);
        Assert.DoesNotContain(em.Adjustments, a => a.Modifier == "51");

        // Highest eligible surgery at 100%, second at 50%
        Assert.Equal(800m, resultSet.LineResults.Single(r => r.ProcedureCode == "29881").AllowedAmount);
        Assert.Equal(100m, resultSet.LineResults.Single(r => r.ProcedureCode == "20610").AllowedAmount);
    }

    /// <summary>
    /// CMS: 6th and subsequent procedures are "by report". The engine prices
    /// them at 50% and flags the adjustment for review.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_SixthProcedure_PricedAt50AndFlaggedByReport()
    {
        var codes = new[] { "10001", "10002", "10003", "10004", "10005", "10006" };
        var schedule = new FeeSchedule
        {
            Id = "comm-six", TenantId = Tenant, Name = "Commercial Six",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = codes.Select((c, i) => new FeeScheduleLine { ProcedureCode = c, Rate = 600m - i * 100m }).ToList()
        };
        var engine = CreateEngine(schedule);

        var requests = codes
            .Select((c, i) => CreateRequest(c, lineNumber: i + 1, totalLines: codes.Length))
            .ToList();

        var resultSet = await engine.ResolveBatchAsync(requests);

        Assert.Equal(600m, resultSet.LineResults[0].AllowedAmount);
        Assert.Equal([250m, 200m, 150m, 100m],
            resultSet.LineResults.Skip(1).Take(4).Select(r => r.AllowedAmount).ToArray());

        var sixth = resultSet.LineResults[5];
        Assert.Equal(50m, sixth.AllowedAmount);
        var adj = Assert.Single(sixth.Adjustments, a => a.Modifier == "51");
        Assert.Contains("by report", adj.Description);
        Assert.DoesNotContain(resultSet.LineResults.Take(5).SelectMany(r => r.Adjustments),
            a => a.Description.Contains("by report"));
    }

    /// <summary>
    /// A secondary line billed with modifier 51 is reduced once by batch ranking,
    /// not again by the per-line modifier-51 rule.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_Modifier51InBatch_NotReducedTwice()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-51", TenantId = Tenant, Name = "Commercial 51",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", Rate = 1500m },
                new FeeScheduleLine { ProcedureCode = "29881", Rate = 800m },
            ]
        };
        var engine = CreateEngine(schedule);

        var requests = new List<PricingRequest>
        {
            CreateRequest("27447", lineNumber: 1, totalLines: 2),
            CreateRequest("29881", lineNumber: 2, totalLines: 2, modifiers: ["51"]),
        };

        var resultSet = await engine.ResolveBatchAsync(requests);

        var secondary = resultSet.LineResults.Single(r => r.ProcedureCode == "29881");
        Assert.Equal(400m, secondary.AllowedAmount);
        Assert.Single(secondary.Adjustments, a => a.Modifier == "51");
    }

    /// <summary>
    /// Single line batch — no reduction applied.
    /// </summary>
    [Fact]
    public async Task MultipleProcedure_SingleLine_NoReduction()
    {
        var schedule = CreateCommercialSchedule("99213", 125m);
        var engine = CreateEngine(schedule);

        var requests = new List<PricingRequest> { CreateRequest("99213", billed: 200m) };
        var resultSet = await engine.ResolveBatchAsync(requests);

        Assert.Equal(125m, resultSet.LineResults.Single().AllowedAmount);
    }

    /// <summary>
    /// Batch pricing must preserve each request's original LineNumber on the
    /// corresponding result so callers can key results by line.
    /// </summary>
    [Fact]
    public async Task Batch_PreservesOriginalLineNumbers()
    {
        var schedule = new FeeSchedule
        {
            Id = "comm-lines", TenantId = Tenant, Name = "Commercial Lines",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", Rate = 1500m },
                new FeeScheduleLine { ProcedureCode = "29881", Rate = 800m },
                new FeeScheduleLine { ProcedureCode = "20610", Rate = 200m },
            ]
        };
        var engine = CreateEngine(schedule);

        var requests = new List<PricingRequest>
        {
            CreateRequest("20610", lineNumber: 1, totalLines: 3, billed: 300m),
            CreateRequest("27447", lineNumber: 2, totalLines: 3, billed: 2000m),
            CreateRequest("29881", lineNumber: 3, totalLines: 3, billed: 1000m),
        };

        var resultSet = await engine.ResolveBatchAsync(requests);

        // Each result carries the line number of its originating request …
        Assert.Equal(1, resultSet.LineResults.First(r => r.ProcedureCode == "20610").LineNumber);
        Assert.Equal(2, resultSet.LineResults.First(r => r.ProcedureCode == "27447").LineNumber);
        Assert.Equal(3, resultSet.LineResults.First(r => r.ProcedureCode == "29881").LineNumber);

        // … so line numbers are unique and can safely key a dictionary.
        var lineNumbers = resultSet.LineResults.Select(r => r.LineNumber).ToList();
        Assert.Equal(lineNumbers.Count, lineNumbers.Distinct().Count());
    }

    // ═══════════════════════════════════════════════════════════════════
    // PER DIEM
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PerDiem_CalculatesRateTimesLos()
    {
        var schedule = new FeeSchedule
        {
            Id = "perdiem-2026", TenantId = Tenant, Name = "Per Diem",
            Type = FeeScheduleType.PerDiem,
            EffectiveDate = new DateTime(2026, 1, 1),
            PerDiemRate = 2500m,
            Lines = [new FeeScheduleLine { ProcedureCode = "99223", Rate = 2500m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99223", los: 5));

        Assert.Equal(12500m, result.AllowedAmount); // 2500 × 5
        Assert.Equal(RateSource.PerDiem, result.RateSource);
    }

    /// <summary>
    /// Institutional per-diem lines carry units = days; with LOS supplied the
    /// amount is rate × LOS and units are not applied again (not 2500 × 5 × 5).
    /// </summary>
    [Fact]
    public async Task PerDiem_WithLos_UnitsNotAppliedAgain()
    {
        var schedule = new FeeSchedule
        {
            Id = "perdiem-units", TenantId = Tenant, Name = "Per Diem",
            Type = FeeScheduleType.PerDiem,
            EffectiveDate = new DateTime(2026, 1, 1),
            PerDiemRate = 2500m,
            Lines = [new FeeScheduleLine { ProcedureCode = "0120", Rate = 2500m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("0120", los: 5, units: 5));

        Assert.Equal(12500m, result.AllowedAmount);
    }

    /// <summary>Without LOS, units are the day count: 2500 × 4 = 10000.</summary>
    [Fact]
    public async Task PerDiem_WithoutLos_UsesUnitsAsDays()
    {
        var schedule = new FeeSchedule
        {
            Id = "perdiem-nolos", TenantId = Tenant, Name = "Per Diem",
            Type = FeeScheduleType.PerDiem,
            EffectiveDate = new DateTime(2026, 1, 1),
            PerDiemRate = 2500m,
            Lines = [new FeeScheduleLine { ProcedureCode = "0120", Rate = 2500m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("0120", units: 4));

        Assert.Equal(10000m, result.AllowedAmount);
        Assert.Equal(RateSource.PerDiem, result.RateSource);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CAPITATION
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Capitation_AllowedAmountIsZero()
    {
        var schedule = new FeeSchedule
        {
            Id = "cap-2026", TenantId = Tenant, Name = "Capitation",
            Type = FeeScheduleType.Capitation,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 0m }]
        };
        var engine = CreateEngine(schedule);

        var result = await engine.ResolveAsync(CreateRequest("99213"));

        Assert.Equal(0m, result.AllowedAmount);
        Assert.Equal(RateSource.Capitation, result.RateSource);
    }

    // ═══════════════════════════════════════════════════════════════════
    // REPOSITORY CACHE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CachingRepository_CachesScheduleReads()
    {
        var schedule = CreateCommercialSchedule("99213", 85m);
        var inner = new CountingFeeScheduleRepo(schedule);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingFeeScheduleRepository(inner, inner, cache);

        var first = await sut.GetByIdAsync(Tenant, schedule.Id);
        var second = await sut.GetByIdAsync(Tenant, schedule.Id);

        Assert.Same(first, second);
        Assert.Equal(1, inner.GetByIdCalls);
    }

    [Fact]
    public async Task CachingRepository_CachesDefaultScheduleAndContractMisses()
    {
        var inner = new CountingFeeScheduleRepo(defaultSchedule: null);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingFeeScheduleRepository(inner, inner, cache);

        var firstDefault = await sut.GetDefaultForPlanAsync(Tenant, PlanId, new DateTime(2026, 3, 8));
        var secondDefault = await sut.GetDefaultForPlanAsync(Tenant, PlanId, new DateTime(2026, 3, 8));
        var firstContract = await sut.GetContractAsync(Tenant, ProviderNpi, PlanId, new DateTime(2026, 3, 8));
        var secondContract = await sut.GetContractAsync(Tenant, ProviderNpi, PlanId, new DateTime(2026, 3, 8));

        Assert.Null(firstDefault);
        Assert.Null(secondDefault);
        Assert.Null(firstContract);
        Assert.Null(secondContract);
        Assert.Equal(1, inner.GetDefaultForPlanCalls);
        Assert.Equal(1, inner.GetContractCalls);
    }

    [Fact]
    public async Task CachingRepository_UpsertSchedule_InvalidatesDefaultPlanCache()
    {
        var inner = new CountingFeeScheduleRepo(defaultSchedule: null);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingFeeScheduleRepository(inner, inner, cache);

        var firstDefault = await sut.GetDefaultForPlanAsync(Tenant, PlanId, new DateTime(2026, 3, 8));
        var secondDefault = await sut.GetDefaultForPlanAsync(Tenant, PlanId, new DateTime(2026, 3, 8));
        Assert.Null(firstDefault);
        Assert.Null(secondDefault);
        Assert.Equal(1, inner.GetDefaultForPlanCalls);

        var newDefault = CreateCommercialSchedule("99213", 120m);
        await sut.UpsertAsync(newDefault);

        var refreshedDefault = await sut.GetDefaultForPlanAsync(Tenant, PlanId, new DateTime(2026, 3, 8));
        Assert.NotNull(refreshedDefault);
        Assert.Equal(2, inner.GetDefaultForPlanCalls);
    }

    [Fact]
    public async Task CachingRepository_UpsertContract_InvalidatesContractCache()
    {
        var inner = new CountingFeeScheduleRepo(defaultSchedule: null);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingFeeScheduleRepository(inner, inner, cache);
        var serviceDate = new DateTime(2026, 3, 8);

        var firstContract = await sut.GetContractAsync(Tenant, ProviderNpi, PlanId, serviceDate);
        var secondContract = await sut.GetContractAsync(Tenant, ProviderNpi, PlanId, serviceDate);
        Assert.Null(firstContract);
        Assert.Null(secondContract);
        Assert.Equal(1, inner.GetContractCalls);

        var updatedContract = new ProviderContract
        {
            Id = ProviderContract.MakeId(Tenant, ProviderNpi, PlanId),
            TenantId = Tenant,
            ProviderNpi = ProviderNpi,
            PlanId = PlanId,
            EffectiveDate = new DateTime(2026, 1, 1),
            NetworkStatus = NetworkStatus.InNetwork,
            FeeScheduleId = "comm-test"
        };
        await sut.UpsertAsync(updatedContract);

        var refreshedContract = await sut.GetContractAsync(Tenant, ProviderNpi, PlanId, serviceDate);
        Assert.NotNull(refreshedContract);
        Assert.Equal(2, inner.GetContractCalls);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static FeeSchedule CreateMpfsSchedule(
        string procedureCode,
        decimal workRvu, decimal peRvu, decimal peRvuFacility, decimal mpRvu,
        decimal cf,
        decimal workGpci = 1.0m, decimal peGpci = 1.0m, decimal mpGpci = 1.0m)
    {
        return new FeeSchedule
        {
            Id = "mpfs-test", TenantId = Tenant, Name = "MPFS Test",
            Type = FeeScheduleType.MedicareMpfs,
            EffectiveDate = new DateTime(2026, 1, 1),
            ConversionFactor = cf,
            WorkGpci = workGpci, PeGpci = peGpci, MpGpci = mpGpci,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = procedureCode,
                    RateType = FeeScheduleRateType.Rvu,
                    WorkRvu = workRvu, PeRvu = peRvu, PeRvuFacility = peRvuFacility, MpRvu = mpRvu
                }
            ]
        };
    }

    private static FeeSchedule CreateCommercialSchedule(string procedureCode, decimal rate)
    {
        return new FeeSchedule
        {
            Id = "comm-test", TenantId = Tenant, Name = "Commercial Test",
            Type = FeeScheduleType.Commercial,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = procedureCode, Rate = rate }]
        };
    }

    private static PricingRequest CreateRequest(
        string procedureCode,
        string pos = "11",
        decimal billed = 200m,
        string? drgCode = null,
        int? los = null,
        int lineNumber = 1,
        int totalLines = 1,
        List<string>? modifiers = null,
        decimal units = 1)
    {
        return new PricingRequest
        {
            TenantId = Tenant,
            ProcedureCode = procedureCode,
            Modifiers = modifiers ?? [],
            ProviderNpi = ProviderNpi,
            PlaceOfServiceCode = pos,
            ServiceDate = new DateTime(2026, 3, 8),
            PlanId = PlanId,
            BilledAmount = billed,
            Units = units,
            LineNumber = lineNumber,
            TotalLineCount = totalLines,
            DrgCode = drgCode,
            LengthOfStay = los,
        };
    }

    private static RateResolutionService CreateEngine(
        FeeSchedule? schedule,
        InMemoryFeeScheduleRepo? repo = null)
    {
        repo ??= new InMemoryFeeScheduleRepo(schedule);
        var contractRepo = new InMemoryProviderContractRepo(schedule?.Id);
        return new RateResolutionService(repo, contractRepo,
            NullLogger<RateResolutionService>.Instance);
    }
}

// ═══════════════════════════════════════════════════════════════════
// TEST DOUBLES
// ═══════════════════════════════════════════════════════════════════

internal class InMemoryFeeScheduleRepo : IFeeScheduleRepository
{
    private readonly Dictionary<string, FeeSchedule> _schedules = new();
    private readonly string? _defaultId;

    public InMemoryFeeScheduleRepo(FeeSchedule? defaultSchedule)
    {
        if (defaultSchedule is not null)
        {
            _schedules[defaultSchedule.Id] = defaultSchedule;
            _defaultId = defaultSchedule.Id;
        }
    }

    public void AddSchedule(FeeSchedule schedule)
        => _schedules[schedule.Id] = schedule;

    public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct)
        => Task.FromResult(_schedules.GetValueOrDefault(id));

    public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct)
        => Task.FromResult(_defaultId is not null ? _schedules.GetValueOrDefault(_defaultId) : null);

    public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct)
        => Task.FromResult<FeeScheduleLine?>(null);

    public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct)
    {
        _schedules[schedule.Id] = schedule;
        return Task.FromResult(schedule);
    }

    public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page, int pageSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FeeSchedule>>(_schedules.Values.ToList());
}

internal class InMemoryProviderContractRepo : IProviderContractRepository
{
    private readonly string? _feeScheduleId;

    public InMemoryProviderContractRepo(string? feeScheduleId)
        => _feeScheduleId = feeScheduleId;

    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct)
    {
        if (_feeScheduleId is null)
            return Task.FromResult<ProviderContract?>(null);

        return Task.FromResult<ProviderContract?>(new ProviderContract
        {
            Id = "contract-test",
            TenantId = tenantId,
            ProviderNpi = providerNpi,
            PlanId = planId,
            NetworkStatus = NetworkStatus.InNetwork,
            FeeScheduleId = _feeScheduleId,
            EffectiveDate = new DateTime(2026, 1, 1),
        });
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct)
        => Task.FromResult(contract);

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderContract>>([]);
}

internal class CountingFeeScheduleRepo : IFeeScheduleRepository, IProviderContractRepository
{
    private FeeSchedule? _defaultSchedule;
    private ProviderContract? _contract;

    public int GetByIdCalls { get; private set; }
    public int GetDefaultForPlanCalls { get; private set; }
    public int GetContractCalls { get; private set; }

    public CountingFeeScheduleRepo(FeeSchedule? defaultSchedule)
        => _defaultSchedule = defaultSchedule;

    public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct)
    {
        GetByIdCalls++;
        return Task.FromResult(_defaultSchedule?.Id == id ? _defaultSchedule : null);
    }

    public Task<FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId, string planId, DateTime serviceDate, CancellationToken ct)
    {
        GetDefaultForPlanCalls++;
        return Task.FromResult(_defaultSchedule);
    }

    public Task<FeeScheduleLine?> GetLineAsync(
        string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct)
        => Task.FromResult<FeeScheduleLine?>(null);

    public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct)
    {
        _defaultSchedule = schedule;
        return Task.FromResult(schedule);
    }

    public Task<IReadOnlyList<FeeSchedule>> ListAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FeeSchedule>>([]);

    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct)
    {
        GetContractCalls++;
        return Task.FromResult(_contract);
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct)
    {
        _contract = contract;
        return Task.FromResult(contract);
    }

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderContract>>([]);
}
