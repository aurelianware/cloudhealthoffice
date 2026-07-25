namespace EnrollmentImportService.Clients;

/// <summary>
/// Client for sponsor-service's own Sponsor API — same rationale as
/// IMemberServiceClient: enrollment-import-service used to write Sponsor
/// documents directly into a shared Mongo collection from before
/// sponsor-service was split out on its own.
/// </summary>
public interface ISponsorServiceClient
{
    /// <summary>True if a sponsor with this group number already exists for the tenant.</summary>
    Task<bool> ExistsAsync(string tenantId, string groupNumber, CancellationToken ct = default);

    Task CreateAsync(string tenantId, CreateSponsorRequestDto request, CancellationToken ct = default);
}

/// <summary>Mirrors sponsor-service's CreateSponsorRequest (SponsorsController.cs).</summary>
public class CreateSponsorRequestDto
{
    public string GroupNumber { get; set; } = string.Empty;
    public string EmployerName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}
