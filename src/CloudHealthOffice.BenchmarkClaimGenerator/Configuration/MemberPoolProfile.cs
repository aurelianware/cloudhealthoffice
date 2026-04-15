namespace CloudHealthOffice.BenchmarkClaimGenerator.Configuration;

/// <summary>
/// Configuration parameters controlling synthetic member pool generation.
/// </summary>
public class MemberPoolProfile
{
    /// <summary>Number of subscriber (primary) members to generate. Default: 50,000.</summary>
    public int SubscriberCount { get; set; } = 50_000;

    /// <summary>Target total member count including dependents. Default: ~75,000.</summary>
    public int TargetTotalMembers { get; set; } = 75_000;

    /// <summary>Random seed for deterministic generation.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Tenant identifier for all generated members.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Group number prefix for sponsors.</summary>
    public string GroupNumberPrefix { get; set; } = "MCC-GRP";

    /// <summary>
    /// Percentage of subscribers with Active enrollment status (0.0-1.0). Default: 0.95.
    /// The remaining fraction (1.0 - ActiveRate) will be generated as Terminated members.
    /// </summary>
    public double ActiveRate { get; set; } = 0.95;

    /// <summary>
    /// Dependent distribution: fraction of subscribers with 0, 1, 2-3, 4+ dependents.
    /// Must sum to 1.0.
    /// </summary>
    public DependentDistribution DependentDistribution { get; set; } = new();

    /// <summary>
    /// Age distribution: fraction in each age bracket.
    /// Must sum to 1.0.
    /// </summary>
    public AgeDistribution AgeDistribution { get; set; } = new();

    /// <summary>
    /// Insurance line distribution: fraction of members with each combination.
    /// </summary>
    public InsuranceLineDistribution InsuranceLines { get; set; } = new();

    /// <summary>Earliest coverage effective date for staggering. Default: 36 months ago.</summary>
    public DateTime EarliestCoverageDate { get; set; } = DateTime.Today.AddMonths(-36);

    /// <summary>Latest coverage effective date. Default: today.</summary>
    public DateTime LatestCoverageDate { get; set; } = DateTime.Today;
}

/// <summary>
/// Distribution controlling how many dependents each subscriber has.
/// </summary>
public class DependentDistribution
{
    /// <summary>Fraction with 0 dependents (single coverage). Default: 0.60.</summary>
    public double ZeroDependents { get; set; } = 0.60;

    /// <summary>Fraction with 1 dependent (spouse). Default: 0.20.</summary>
    public double OneDependents { get; set; } = 0.20;

    /// <summary>Fraction with 2-3 dependents (spouse + children). Default: 0.15.</summary>
    public double TwoThreeDependents { get; set; } = 0.15;

    /// <summary>Fraction with 4+ dependents (large families). Default: 0.05.</summary>
    public double FourPlusDependents { get; set; } = 0.05;
}

/// <summary>
/// Age distribution brackets for subscriber generation.
/// </summary>
public class AgeDistribution
{
    /// <summary>Fraction age 0-17. Default: 0.25.</summary>
    public double Under18 { get; set; } = 0.25;

    /// <summary>Fraction age 18-44. Default: 0.35.</summary>
    public double Age18To44 { get; set; } = 0.35;

    /// <summary>Fraction age 45-64. Default: 0.30.</summary>
    public double Age45To64 { get; set; } = 0.30;

    /// <summary>Fraction age 65+. Default: 0.10.</summary>
    public double Age65Plus { get; set; } = 0.10;
}

/// <summary>
/// Distribution of insurance line code combinations.
/// </summary>
public class InsuranceLineDistribution
{
    /// <summary>Fraction with HLT (medical) only. Default: 0.85.</summary>
    public double HealthOnly { get; set; } = 0.85;

    /// <summary>Fraction with HLT + DEN (medical + dental). Default: 0.10.</summary>
    public double HealthAndDental { get; set; } = 0.10;

    /// <summary>Fraction with HLT + DEN + VIS (all three). Default: 0.05.</summary>
    public double HealthDentalVision { get; set; } = 0.05;
}
