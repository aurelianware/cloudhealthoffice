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

    /// <summary>
    /// The sponsor's line of business as sponsor-service reports it (its enum,
    /// by number or name), or null when the sponsor is not on file.
    /// </summary>
    Task<string?> GetLineOfBusinessAsync(string tenantId, string groupNumber, CancellationToken ct = default);

    /// <summary>
    /// What an inbound 834 REF*1L actually names: a group number, a policy
    /// number of a group, more than one group's policy number, or nothing on
    /// file (docs/architecture/plan-configuration.md §9b).
    ///
    /// <para>
    /// sponsor-service tries the group number first and lets it win outright,
    /// so every value that resolves today still resolves to the same sponsor.
    /// </para>
    /// </summary>
    Task<SponsorIdentifier> ResolveIdentifierAsync(string tenantId, string value, CancellationToken ct = default);
}

/// <summary>Which of the two numbers sponsor-service recognised, if either.</summary>
public enum SponsorIdentifierKind
{
    /// <summary>Neither a group number nor a policy number of this tenant.</summary>
    Unknown,

    /// <summary>A sponsor's own group number — what REF*1L has always meant.</summary>
    GroupNumber,

    /// <summary>A policy number, which identifies both its policy and its group.</summary>
    PolicyNumber,

    /// <summary>
    /// A policy number carried by more than one group. Unique within a group
    /// and nothing makes it unique across them, so this is reachable state
    /// rather than corruption, and it does not say which employer is meant.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// Mirrors sponsor-service's GET /api/v1/sponsors/resolve-identifier.
/// GroupNumber is the group to file against for either recognised kind;
/// PolicyNumber is set only when the value was a policy number.
/// </summary>
public sealed record SponsorIdentifier(
    SponsorIdentifierKind Kind,
    string? GroupNumber = null,
    string? PolicyNumber = null,
    IReadOnlyList<string>? CandidateGroupNumbers = null);

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
