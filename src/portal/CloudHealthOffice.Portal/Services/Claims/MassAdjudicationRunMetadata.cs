namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationRunMetadata
{
    public string TenantId { get; set; } = string.Empty;
    public int RequestedClaims { get; set; }
    public int Seed { get; set; }
    public int Parallelism { get; set; }
    public string ClaimsUrl { get; set; } = string.Empty;
    public string BenefitUrl { get; set; } = string.Empty;
    public string MemberUrl { get; set; } = string.Empty;
    public string CoverageUrl { get; set; } = string.Empty;
    public string ProviderUrl { get; set; } = string.Empty;
    public bool SeedMembers { get; set; }
    public bool SeedProviders { get; set; }
    public bool SkipClaimUpdate { get; set; }
    public int LineOfBusiness { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
