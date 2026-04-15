using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates synthetic benefit plan configurations for benchmark testing.
/// Creates Texas Medicaid plan templates with realistic cost-sharing rules.
/// </summary>
public static class SyntheticBenefitPlanGenerator
{
    /// <summary>
    /// Generate all benefit plan configurations.
    /// </summary>
    /// <param name="seed">Random seed (unused — plans are deterministic templates).</param>
    /// <param name="effectiveDate">Plan effective date. Default: January 1, 2024.</param>
    /// <returns>List of benefit plan configurations.</returns>
    public static List<SyntheticBenefitPlan> Generate(int seed = 42, DateTime? effectiveDate = null)
    {
        var effDate = effectiveDate ?? new DateTime(2024, 1, 1);
        return BenefitPlanTemplates.CreateAll(effDate);
    }

    /// <summary>
    /// Get a plan by its plan ID.
    /// </summary>
    public static SyntheticBenefitPlan? GetPlan(List<SyntheticBenefitPlan> plans, string planId)
    {
        return plans.FirstOrDefault(p => p.PlanId == planId);
    }

    /// <summary>
    /// Get the copay amount for a given plan and service category.
    /// </summary>
    public static decimal GetCopay(SyntheticBenefitPlan plan, string claimType)
    {
        return claimType.ToLowerInvariant() switch
        {
            "professional" or "officevisit" or "office" => plan.PcpCopay,
            "specialist" => plan.SpecialistCopay,
            "emergency" => plan.ErCopay,
            "inpatient" or "institutional" => plan.InpatientCopay,
            _ => plan.PcpCopay,
        };
    }

    /// <summary>
    /// Get the coinsurance percentage for a given plan.
    /// </summary>
    public static decimal GetCoinsurance(SyntheticBenefitPlan plan)
    {
        return plan.CoinsurancePercent;
    }

    /// <summary>
    /// Determine if a claim requires prior authorization based on the plan configuration.
    /// </summary>
    public static bool RequiresPriorAuth(SyntheticBenefitPlan plan, string serviceCategory)
    {
        var benefit = plan.Benefits.FirstOrDefault(b =>
            b.ServiceCategory.Contains(serviceCategory, StringComparison.OrdinalIgnoreCase));
        return benefit?.PriorAuthRequired ?? false;
    }
}
