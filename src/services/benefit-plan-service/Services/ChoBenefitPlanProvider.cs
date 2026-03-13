using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using EnginePlanType = CloudHealthOffice.BenefitEngine.Domain.PlanType;
using ModelPlanType = BenefitPlanService.Models.PlanType;

namespace BenefitPlanService.Services;

/// <summary>
/// Real IBenefitPlanProvider backed by IBenefitPlanRepository (MongoDB or Cosmos).
/// Loaded by Program.cs in place of the engine's internal stub.
/// </summary>
public class ChoBenefitPlanProvider : IBenefitPlanProvider
{
    private readonly IBenefitPlanRepository _repo;
    private readonly IBenefitEngineTenantContext _tenantContext;

    public ChoBenefitPlanProvider(IBenefitPlanRepository repo, IBenefitEngineTenantContext tenantContext)
    {
        _repo = repo;
        _tenantContext = tenantContext;
    }

    public async Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default)
    {
        var plan = await _repo.GetByIdAsync(benefitPlanId.ToString(), _tenantContext.TenantId);
        if (plan is null)
            return null;

        return MapToConfig(plan);
    }

    private static BenefitPlanConfig MapToConfig(BenefitPlan plan)
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

        return new BenefitPlanConfig
        {
            Id = Guid.Parse(plan.Id),
            TenantId = plan.TenantId,
            PlanName = plan.PlanName,
            PlanType = MapPlanType(plan.PlanType),
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
            IsHdhp = plan.PlanType == ModelPlanType.HDHP,
            Categories = categories
        };
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
