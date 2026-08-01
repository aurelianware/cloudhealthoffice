using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Observability;
using EnginePlanType = CloudHealthOffice.BenefitEngine.Domain.PlanType;
using ModelPlanType = BenefitPlanService.Models.PlanType;
using EngineFamilyAccumulatorModel = CloudHealthOffice.BenefitEngine.Domain.FamilyAccumulatorModel;
using ModelFamilyAccumulatorModel = BenefitPlanService.Models.FamilyAccumulatorModel;
using Microsoft.Extensions.Caching.Memory;

namespace BenefitPlanService.Services;

/// <summary>
/// Real IBenefitPlanProvider backed by IBenefitPlanRepository (MongoDB or Cosmos).
/// Loaded by Program.cs in place of the engine's internal stub.
/// </summary>
public class ChoBenefitPlanProvider : IBenefitPlanProvider
{
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        SlidingExpiration = TimeSpan.FromMinutes(2)
    };

    /// <summary>
    /// Cutoff that distinguishes legacy plans (hydrate with
    /// <c>IsAcaCapEnforced=false</c>) from post-5.7 publishes (which set
    /// it true automatically). Re-exported here for callers that already
    /// reference <see cref="ChoBenefitPlanProvider"/>; the canonical
    /// definition lives on <see cref="AcaCapEnforcementPolicy.CutoffUtc"/>
    /// because BP 5.8 surfaces the same enforcement state through the
    /// FHIR InsurancePlan projector and both call sites must agree on
    /// the wall-clock instant.
    /// </summary>
    public static DateTime AcaCapEnforcementCutoffUtc => AcaCapEnforcementPolicy.CutoffUtc;

    private readonly IBenefitPlanRepository _repo;
    private readonly IBenefitEngineTenantContext _tenantContext;
    private readonly IAcaLimitsProvider _acaLimits;
    private readonly IPlanYearResolver _planYear;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ChoBenefitPlanProvider> _logger;

    public ChoBenefitPlanProvider(
        IBenefitPlanRepository repo,
        IBenefitEngineTenantContext tenantContext,
        IAcaLimitsProvider acaLimits,
        IPlanYearResolver planYear,
        IMemoryCache cache,
        ILogger<ChoBenefitPlanProvider> logger)
    {
        _repo = repo;
        _tenantContext = tenantContext;
        _acaLimits = acaLimits;
        _planYear = planYear;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default)
    {
        var cacheKey = $"benefit-plan-config:{_tenantContext.TenantId}:{benefitPlanId:N}";
        if (_cache.TryGetValue<BenefitPlanConfig>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var plan = await _repo.GetByIdAsync(benefitPlanId.ToString(), _tenantContext.TenantId);
        if (plan is null)
            return null;

        var config = MapToConfig(plan);
        _cache.Set(cacheKey, config, CacheOptions);
        return config;
    }

    internal BenefitPlanConfig MapToConfig(BenefitPlan plan)
    {
        // BP 5.10: project every Benefit to its own BenefitCategoryConfig
        // and carry the originating BenefitRulePredicate (if any) so the
        // engine's rule gate can pick the right benefit per encounter.
        // Order matches BenefitPlan.Benefits order — the first authored
        // benefit wins when no predicate gates the choice.
        var categories = plan.Benefits
            .Select(b => new BenefitCategoryConfig
            {
                ServiceTypeCode = b.ServiceCategory,
                ServiceTypeDescription = b.Description,
                IsCovered = b.IsCovered,
                AuthRequired = b.PriorAuthRequired || b.RequiresPriorAuth,
                VisitLimit = b.VisitLimit,
                DollarLimit = b.AnnualMaximum ?? b.LifetimeMaximum,
                InNetworkCostSharing = BuildInNetworkCostSharing(b),
                OutOfNetworkCostSharing = BuildOutOfNetworkCostSharing(b),
                Predicate = ProjectPredicate(plan, b),
            })
            .ToList();

        // Resolve ACA per-member cap for the plan year (capability 5.7).
        // Lookup is best-effort here — when missing, the engine simply
        // doesn't seed the cap accumulator. Hard rejection lives in
        // IPlanLimitValidator at write time, which is the right gate for
        // plan correctness; the read path stays soft so legacy plans
        // hydrate even if the ops file falls behind.
        var planYear = _planYear.Resolve(plan);
        var acaCaps = _acaLimits.GetForPlanYear(planYear);
        if (acaCaps is null)
        {
            _logger.LogWarning(
                "ACA OOP limits not configured for plan year {PlanYear}; engine will receive null AcaIndividualCap for plan {PlanId} version {VersionId}",
                planYear,
                SanitizeForLog(plan.PlanId),
                SanitizeForLog(plan.VersionId));
        }

        return new BenefitPlanConfig
        {
            Id = Guid.Parse(plan.Id),
            TenantId = plan.TenantId,
            PlanName = plan.PlanName,
            PlanType = MapPlanType(plan.PlanType),
            PlanYear = planYear.ToString(),
            LineOfBusiness = plan.LineOfBusiness.ToString(),
            IndividualDeductible = plan.CostSharing.IndividualDeductible > 0
                ? plan.CostSharing.IndividualDeductible
                : plan.CostSharing.InNetworkDeductible > 0
                    ? plan.CostSharing.InNetworkDeductible
                    : null,
            FamilyDeductible = plan.CostSharing.FamilyDeductible > 0
                ? plan.CostSharing.FamilyDeductible
                : null,
            IndividualOopMax = plan.CostSharing.IndividualOutOfPocketMax > 0
                ? plan.CostSharing.IndividualOutOfPocketMax
                : plan.CostSharing.InNetworkOutOfPocketMax > 0
                    ? plan.CostSharing.InNetworkOutOfPocketMax
                    : null,
            FamilyOopMax = plan.CostSharing.FamilyOutOfPocketMax > 0
                ? plan.CostSharing.FamilyOutOfPocketMax
                : null,
            IndividualDeductibleOon = plan.CostSharing.OutNetworkIndividualDeductible
                ?? (plan.CostSharing.OutOfNetworkDeductible > 0 ? plan.CostSharing.OutOfNetworkDeductible : null),
            FamilyDeductibleOon = plan.CostSharing.OutNetworkFamilyDeductible,
            IndividualOopMaxOon = plan.CostSharing.OutNetworkIndividualOutOfPocketMax
                ?? (plan.CostSharing.OutOfNetworkOutOfPocketMax > 0 ? plan.CostSharing.OutOfNetworkOutOfPocketMax : null),
            FamilyOopMaxOon = plan.CostSharing.OutNetworkFamilyOutOfPocketMax,
            FamilyAccumulatorModel = MapFamilyAccumulatorModel(plan.FamilyAccumulatorModel),
            AcaIndividualCap = acaCaps?.IndividualCap,
            IsAcaCapEnforced = ResolveIsAcaCapEnforced(plan),
            IsHdhp = plan.PlanType == ModelPlanType.HDHP,
            Categories = categories
        };
    }

    /// <summary>
    /// G8 gated rollout. Delegates to <see cref="AcaCapEnforcementPolicy.IsEnforced"/>
    /// so the engine-config projection and the FHIR InsurancePlan
    /// projection (BP 5.8) share one decision rule.
    /// </summary>
    private static bool ResolveIsAcaCapEnforced(BenefitPlan plan)
        => AcaCapEnforcementPolicy.IsEnforced(plan);

    /// <summary>
    /// Capability BP 5.10 — project the originating
    /// <see cref="BenefitRulePredicate"/> from <see cref="Benefit.Rules"/>
    /// onto the engine config. Multi-predicate-AND semantics is a
    /// Phase 2 capability (Decision 4); for now we collapse to the
    /// first non-null entry and emit a counter so operators see when
    /// multi-predicate authoring is happening in the wild.
    /// </summary>
    private BenefitRulePredicate? ProjectPredicate(BenefitPlan plan, Benefit benefit)
    {
        var rules = benefit.Rules;
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        if (rules.Count > 1)
        {
            ChoMetrics.PredicateMultiRuleTruncated.Add(1,
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId));
            _logger.LogWarning(
                "Benefit projection collapsed multi-predicate rules to first entry (Phase 2): tenant={Tenant} planId={PlanId} versionId={VersionId} benefitId={BenefitId} ruleCount={RuleCount}",
                SanitizeForLog(plan.TenantId),
                SanitizeForLog(plan.PlanId),
                SanitizeForLog(plan.VersionId),
                SanitizeForLog(benefit.Id),
                rules.Count);
        }

        return rules.FirstOrDefault(r => r is not null);
    }

    private static EngineFamilyAccumulatorModel MapFamilyAccumulatorModel(ModelFamilyAccumulatorModel model)
        => model switch
        {
            ModelFamilyAccumulatorModel.Aggregate => EngineFamilyAccumulatorModel.Aggregate,
            _ => EngineFamilyAccumulatorModel.Embedded,
        };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static IReadOnlyList<CostShareRuleConfig> BuildInNetworkCostSharing(Benefit b)
    {
        var rules = new List<CostShareRuleConfig>();

        var copay = b.InNetworkCopay ?? b.CopayAmount;
        if (copay.HasValue)
        {
            rules.Add(new CostShareRuleConfig
            {
                CostShareType = CostShareType.Copay,
                CopayAmount = copay.Value,
                DeductibleApplies = b.DeductibleApplies,
                CopayApplicationMode = b.DeductibleApplies
                    ? CopayApplicationMode.AfterDeductible
                    : CopayApplicationMode.InsteadOfDeductible
            });
        }

        var coins = b.InNetworkCoinsurance ?? b.CoinsurancePercentage;
        if (coins.HasValue)
        {
            rules.Add(new CostShareRuleConfig
            {
                CostShareType = CostShareType.Coinsurance,
                CoinsurancePercent = coins.Value,
                DeductibleApplies = b.DeductibleApplies
            });
        }

        return rules;
    }

    private static IReadOnlyList<CostShareRuleConfig> BuildOutOfNetworkCostSharing(Benefit b)
    {
        var rules = new List<CostShareRuleConfig>();

        if (b.OutNetworkCopay.HasValue)
        {
            rules.Add(new CostShareRuleConfig
            {
                CostShareType = CostShareType.Copay,
                CopayAmount = b.OutNetworkCopay.Value,
                DeductibleApplies = b.DeductibleApplies
            });
        }

        if (b.OutNetworkCoinsurance.HasValue)
        {
            rules.Add(new CostShareRuleConfig
            {
                CostShareType = CostShareType.Coinsurance,
                CoinsurancePercent = b.OutNetworkCoinsurance.Value,
                DeductibleApplies = b.DeductibleApplies
            });
        }

        return rules;
    }

    private static EnginePlanType MapPlanType(ModelPlanType modelType) => modelType switch
    {
        ModelPlanType.HMO => EnginePlanType.HMO,
        ModelPlanType.PPO => EnginePlanType.PPO,
        ModelPlanType.EPO => EnginePlanType.EPO,
        ModelPlanType.POS => EnginePlanType.POS,
        ModelPlanType.HDHP => EnginePlanType.HDHP,
        _ => EnginePlanType.PPO  // Medicare, Medicaid, Commercial default to PPO waterfall
    };
}
