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
    /// <summary>Creates a coverage; returns coverage-service's id for it (null if the response carried none).</summary>
    Task<string?> CreateAsync(string tenantId, CreateCoverageRequestDto request, CancellationToken ct = default);

    /// <summary>Every coverage on file for the member, including terminated ones (coverage-service's history endpoint).</summary>
    Task<IReadOnlyList<CoverageRecordDto>> GetMemberCoverageAsync(string tenantId, string memberId, CancellationToken ct = default);

    /// <summary>Updates plan / coverage level on an existing coverage (coverage-service PUT).</summary>
    Task UpdateAsync(string tenantId, string coverageId, UpdateCoverageRequestDto request, CancellationToken ct = default);

    /// <summary>Sets an existing coverage's termination date (coverage-service DELETE ?terminationDate=).</summary>
    Task TerminateAsync(string tenantId, string coverageId, DateTime terminationDate, string? reasonCode, CancellationToken ct = default);
}

/// <summary>The subset of coverage-service's Coverage document the importer matches on.</summary>
public class CoverageRecordDto
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

/// <summary>Mirrors coverage-service's UpdateCoverageRequest (CoverageController.cs).</summary>
public class UpdateCoverageRequestDto
{
    public string? PlanId { get; set; }
    public string? CoverageLevel { get; set; }
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
