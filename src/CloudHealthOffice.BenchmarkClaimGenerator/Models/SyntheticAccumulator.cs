namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents an accumulator balance for a member's deductible/OOP tracking per plan year.
/// Structurally compatible with the production AccumulatorDocument in BenefitEngine.
/// </summary>
public class SyntheticAccumulator
{
    /// <summary>Composite identifier: {tenantId}:{scope}:{ownerId}:{benefitPlanId}:{planYear}.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Tenant identifier.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Member identifier (for individual scope) or subscriber identifier (for family scope).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Subscriber identifier.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>Benefit plan identifier.</summary>
    public string BenefitPlanId { get; set; } = string.Empty;

    /// <summary>Plan year (e.g., "2024").</summary>
    public string PlanYear { get; set; } = "2024";

    /// <summary>Scope: Individual or Family.</summary>
    public string Scope { get; set; } = "Individual";

    /// <summary>Individual deductible limit amount.</summary>
    public decimal IndividualDeductibleLimit { get; set; }

    /// <summary>Individual deductible amount spent/accumulated.</summary>
    public decimal IndividualDeductibleSpent { get; set; }

    /// <summary>Family deductible limit amount.</summary>
    public decimal FamilyDeductibleLimit { get; set; }

    /// <summary>Family deductible amount spent/accumulated.</summary>
    public decimal FamilyDeductibleSpent { get; set; }

    /// <summary>Individual out-of-pocket maximum limit.</summary>
    public decimal IndividualOopMaxLimit { get; set; }

    /// <summary>Individual out-of-pocket amount spent.</summary>
    public decimal IndividualOopSpent { get; set; }

    /// <summary>Family out-of-pocket maximum limit.</summary>
    public decimal FamilyOopMaxLimit { get; set; }

    /// <summary>Family out-of-pocket amount spent.</summary>
    public decimal FamilyOopSpent { get; set; }

    /// <summary>Network tier: InNetwork, OutOfNetwork.</summary>
    public string NetworkTier { get; set; } = "InNetwork";

    /// <summary>Last updated timestamp.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>Remaining individual deductible.</summary>
    public decimal RemainingIndividualDeductible =>
        Math.Max(0, IndividualDeductibleLimit - IndividualDeductibleSpent);

    /// <summary>Remaining individual OOP max.</summary>
    public decimal RemainingIndividualOop =>
        Math.Max(0, IndividualOopMaxLimit - IndividualOopSpent);
}
