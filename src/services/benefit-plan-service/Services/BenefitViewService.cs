using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

public interface IBenefitViewService
{
    /// <summary>
    /// Build a categorized member view of the plan as of the given service
    /// date. Returns <c>null</c> when the plan is not found.
    /// </summary>
    Task<MemberBenefitView?> GetMemberViewAsync(string planId, string tenantId, DateTime serviceDate);
}

public class BenefitViewService : IBenefitViewService
{
    private readonly IBenefitPlanService _plans;
    private readonly ILogger<BenefitViewService> _logger;

    public BenefitViewService(IBenefitPlanService plans, ILogger<BenefitViewService> logger)
    {
        _plans = plans;
        _logger = logger;
    }

    public async Task<MemberBenefitView?> GetMemberViewAsync(string planId, string tenantId, DateTime serviceDate)
    {
        var plan = await _plans.GetPlanAsync(planId, tenantId);
        if (plan == null)
        {
            return null;
        }

        var view = new MemberBenefitView
        {
            PlanId = plan.PlanId,
            PlanName = plan.PlanName,
            Payer = plan.Payer,
            PlanType = plan.PlanType.ToString(),
            MetalLevel = plan.MetalLevel?.ToString(),
            LineOfBusiness = plan.LineOfBusiness.ToString(),
            AsOfDate = serviceDate.Date,
            EffectiveDate = plan.EffectiveDate,
            TerminationDate = plan.TerminationDate,
            PlanVersion = BuildPlanVersion(plan),
            FamilyAccumulatorModel = plan.FamilyAccumulatorModel.ToString(),
            CostSharing = plan.CostSharing,
            Categories = plan.Benefits.Select(b => ProjectBenefit(b, plan)).ToList(),
            Documents = plan.Documents.Select(ProjectDocument).ToList(),
        };

        return view;
    }

    private CategorizedBenefit ProjectBenefit(Benefit benefit, BenefitPlan plan)
    {
        var (category, mapped) = BenefitCategoryMap.Resolve(benefit.ServiceCategory);
        if (!mapped)
        {
            // Unmapped categories surface here; fix the map when they do.
            _logger.LogInformation(
                "Unmapped benefit service category {ServiceCategory} on plan {PlanId} tenant {TenantId} — defaulting to Other",
                SanitizeForLog(benefit.ServiceCategory),
                SanitizeForLog(plan.PlanId),
                SanitizeForLog(plan.TenantId));
        }

        var inNetworkTierName = plan.NetworkTiers
            .OrderBy(t => t.TierLevel)
            .FirstOrDefault()?.TierName ?? "In-Network";

        var result = new CategorizedBenefit
        {
            Category = category,
            DisplayName = DisplayNameFor(category, benefit),
            ServiceCategory = benefit.ServiceCategory,
            Description = benefit.Description,
            DeductibleApplies = benefit.DeductibleApplies,
            OopApplies = benefit.OopApplies,
            PriorAuthRequired = benefit.PriorAuthRequired || benefit.RequiresPriorAuth,
            VisitLimit = benefit.VisitLimit,
            VisitLimitPeriod = benefit.VisitLimitPeriod,
            AnnualMaximum = benefit.AnnualMaximum,
            LifetimeMaximum = benefit.LifetimeMaximum,
            Limitations = benefit.Limitations,
            InNetwork = new NetworkTierBenefit
            {
                TierName = inNetworkTierName,
                Copay = benefit.InNetworkCopay ?? benefit.CopayAmount,
                Coinsurance = benefit.InNetworkCoinsurance ?? benefit.CoinsurancePercentage,
            },
        };

        if (benefit.OutNetworkCopay.HasValue || benefit.OutNetworkCoinsurance.HasValue)
        {
            result.OutOfNetwork = new NetworkTierBenefit
            {
                TierName = "Out-of-Network",
                Copay = benefit.OutNetworkCopay,
                Coinsurance = benefit.OutNetworkCoinsurance,
            };
        }

        if (category == BenefitCategoryMap.Pharmacy)
        {
            var tierLabel = BenefitCategoryMap.ExtractTierLabel(benefit.ServiceCategory);
            var canonicalTier = BenefitCategoryMap.ExtractCanonicalTier(benefit.ServiceCategory);
            var isSpecialty = BenefitCategoryMap.IsSpecialty(benefit.ServiceCategory);

            if (tierLabel != null || canonicalTier != null || isSpecialty)
            {
                result.Pharmacy = new PharmacyDetail
                {
                    TierLabel = tierLabel,
                    CanonicalTier = canonicalTier,
                    IsSpecialty = isSpecialty,
                };
            }
        }

        return result;
    }

    private static PlanDocumentLink ProjectDocument(PlanDocumentReference d) => new()
    {
        DocType = d.DocType.ToString(),
        DisplayName = d.DisplayName ?? d.DocType.ToString(),
        Location = d.Location,
        ContentType = d.ContentType,
        Size = d.Size,
        ContentHashSha256 = d.ContentHashSha256,
        Version = d.Version,
        EffectiveDate = d.EffectiveDate,
    };

    private static string DisplayNameFor(string category, Benefit benefit)
    {
        // When the category maps cleanly, show a friendly label; when it
        // doesn't, defer to whatever the plan configured so the user still
        // sees something meaningful.
        return category switch
        {
            BenefitCategoryMap.PrimaryCare      => "Primary Care Visit",
            BenefitCategoryMap.Specialist       => "Specialist Visit",
            BenefitCategoryMap.EmergencyRoom    => "Emergency Room",
            BenefitCategoryMap.UrgentCare       => "Urgent Care",
            BenefitCategoryMap.Hospital         => "Hospital Services",
            BenefitCategoryMap.Pharmacy         => string.IsNullOrWhiteSpace(benefit.ServiceCategory) ? "Pharmacy" : benefit.ServiceCategory,
            BenefitCategoryMap.DurableMedical   => "Durable Medical Equipment",
            BenefitCategoryMap.MentalHealth     => "Mental Health / Behavioral Health",
            BenefitCategoryMap.Maternity        => "Maternity",
            BenefitCategoryMap.Preventive       => "Preventive Care",
            _                                   => string.IsNullOrWhiteSpace(benefit.ServiceCategory) ? "Other" : benefit.ServiceCategory,
        };
    }

    private static string BuildPlanVersion(BenefitPlan plan)
    {
        var ts = plan.ModifiedDate ?? plan.UpdatedAt;
        return ts.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
