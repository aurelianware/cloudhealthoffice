namespace CloudHealthOffice.BenchmarkClaimGenerator.Configuration;

/// <summary>
/// Configuration parameters controlling synthetic provider pool generation.
/// </summary>
public class ProviderPoolProfile
{
    /// <summary>Number of individual (Type 1 NPI) providers to generate. Default: 5,000.</summary>
    public int IndividualProviderCount { get; set; } = 5_000;

    /// <summary>Number of organizational (Type 2 NPI) providers to generate. Default: 500.</summary>
    public int OrganizationalProviderCount { get; set; } = 500;

    /// <summary>Random seed for deterministic generation.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Tenant identifier for all generated providers.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Fraction of providers that are in-network (0.0-1.0). Default: 0.80.</summary>
    public double InNetworkRate { get; set; } = 0.80;

    /// <summary>Fraction of providers that are out-of-network. Default: 0.10.</summary>
    public double OutOfNetworkRate { get; set; } = 0.10;

    /// <summary>Fraction of providers that are terminated. Default: 0.10.</summary>
    public double TerminatedRate { get; set; } = 0.10;

    /// <summary>Fraction of individual providers that are PCPs (40%). Default: 0.40.</summary>
    public double PcpFraction { get; set; } = 0.40;

    /// <summary>Facility type distribution for organizational providers.</summary>
    public FacilityDistribution FacilityDistribution { get; set; } = new();

    /// <summary>Number of fee schedules to generate. Default: 3.</summary>
    public int FeeScheduleCount { get; set; } = 3;

    /// <summary>Contract type distribution for in-network providers.</summary>
    public ContractTypeDistribution ContractTypes { get; set; } = new();
}

/// <summary>
/// Distribution of facility types for organizational providers.
/// </summary>
public class FacilityDistribution
{
    /// <summary>Number of hospitals. Default: 200.</summary>
    public int Hospitals { get; set; } = 200;

    /// <summary>Number of clinics/urgent care. Default: 150.</summary>
    public int Clinics { get; set; } = 150;

    /// <summary>Number of skilled nursing facilities. Default: 100.</summary>
    public int SkilledNursingFacilities { get; set; } = 100;

    /// <summary>Number of behavioral health facilities. Default: 50.</summary>
    public int BehavioralHealth { get; set; } = 50;
}

/// <summary>
/// Distribution of contract types for in-network providers.
/// </summary>
public class ContractTypeDistribution
{
    /// <summary>Fraction using fee-for-service contracts. Default: 0.80.</summary>
    public double FeeForService { get; set; } = 0.80;

    /// <summary>Fraction using capitation contracts. Default: 0.15.</summary>
    public double Capitation { get; set; } = 0.15;

    /// <summary>Fraction using per-diem contracts. Default: 0.05.</summary>
    public double PerDiem { get; set; } = 0.05;
}
