namespace EnrollmentImportService.Clients;

/// <summary>
/// Client for coverage-service's own Coverage API — same rationale as
/// IMemberServiceClient/ISponsorServiceClient: enrollment-import-service used
/// to write Coverage documents directly into a Mongo collection shared with
/// coverage-service's own repository (two services, one collection, no
/// ownership boundary). Delegating via HTTP instead, now that PlanId is
/// actually resolved (see IBenefitPlanServiceClient) rather than defaulted.
/// </summary>
public interface ICoverageServiceClient
{
    Task CreateAsync(string tenantId, CreateCoverageRequestDto request, CancellationToken ct = default);
}

/// <summary>Mirrors coverage-service's CreateCoverageRequest (CoverageController.cs).</summary>
public class CreateCoverageRequestDto
{
    public string MemberId { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? MaintenanceTypeCode { get; set; }
}
