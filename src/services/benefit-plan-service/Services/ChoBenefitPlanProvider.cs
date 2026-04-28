using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using EnginePlanType = CloudHealthOffice.BenefitEngine.Domain.PlanType;
using ModelPlanType = BenefitPlanService.Models.PlanType;
using EngineFamilyAccumulatorModel = CloudHealthOffice.BenefitEngine.Domain.FamilyAccumulatorModel;
using ModelFamilyAccumulatorModel = BenefitPlanService.Models.FamilyAccumulatorModel;

namespace BenefitPlanService.Services;

/// <summary>
/// Real IBenefitPlanProvider backed by IBenefitPlanRepository (MongoDB or Cosmos).
/// Loaded by Program.cs in place of the engine's internal stub.
/// </summary>
public class ChoBenefitPlanProvider : IBenefitPlanProvider
{
    /// <summary>
    /// Cutoff that distinguishes legacy plans (hydrate with
    /// <c>IsAcaCapEnforced=false</c>) from post-5.7 publishes (which set
    /// it true automatically). Plans with <see cref="BenefitPlan.PublishedAt"/>
    /// at or after this UTC instant get runtime ACA cap enforcement on
    /// Aggregate mode; legacy plans behave as they did pre-5.7 until an
    /// operator amends + republishes them. Transition support per the
    /// ratified plan (G8); see
    /// <c>docs/architecture/family-accumulator-models.md</c>.
    /// </summary>
    public static readonly DateTime AcaCapEnforcementCutoffUtc =
        new(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

    private readonly IBenefitPlanRepository _repo;
    private readonly IBenefitEngineTenantContext _tenantContext;
    private readonly IAcaLimitsProvider _acaLimits;
    private readonly IPlanYearResolver _planYear;
    private readonly ILogger<ChoBenefitPlanProvider> _logger;

    public ChoBenefitPlanProvider(
        IBenefitPlanRepository repo,
        IBenefitEngineTenantContext tenantContext,
        IAcaLimitsProvider acaLimits,
        IPlanYearResolver planYear,
        ILogger<ChoBenefitPlanProvider> logger)
    {
        _repo = repo;
        _tenantContext = tenantContext;
        _acaLimits = acaLimits;
        _planYear = planYear;
        _logger = logger;
    }

    public async Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default)
    {
        var plan = await _repo.GetByIdAsync(benefitPlanId.ToString(), _tenantContext.TenantId);
        if (plan is null)
            return null;

        return MapToConfig(plan);
    }

    internal BenefitPlanConfig MapToConfig(BenefitPlan plan)
    {
        var categories = plan.Benefits
            .Select(b => new BenefitCategoryConfig
            {
                ServiceTypeCode = b.ServiceCategory,
                ServiceTypeDescription = b.Description,
                IsCovered = true,
                AuthRequired = b.PriorAuthRequired || b.RequiresPriorAuth,
                VisitLimit = b.VisitLimit,
                DollarLimit = b.AnnualMaximum ?? b.LifetimeMaximum,
                InNetworkCostSharing = BuildInNetworkCostSharing(b),
                OutOfNetworkCostSharing = BuildOutOfNetworkCostSharing(b)
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
    /// G8 gated rollout. Plans published at or after the cutoff (or that
    /// have not been published yet, i.e. drafts) get runtime ACA cap
    /// enforcement; legacy plans published before the cutoff hydrate with
    /// enforcement disabled so members on existing Aggregate plans don't
    /// see surprise mid-year caps. Re-publishing a legacy plan flips it
    /// to enforced state automatically because PublishedAt advances.
    /// </summary>
    private static bool ResolveIsAcaCapEnforced(BenefitPlan plan)
    {
        if (plan.FamilyAccumulatorModel != ModelFamilyAccumulatorModel.Aggregate)
            return false;

        if (!plan.PublishedAt.HasValue) return true;

        var publishedAt = plan.PublishedAt.Value.Kind == DateTimeKind.Utc
            ? plan.PublishedAt.Value
            : plan.PublishedAt.Value.ToUniversalTime();

        return publishedAt >= AcaCapEnforcementCutoffUtc;
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
