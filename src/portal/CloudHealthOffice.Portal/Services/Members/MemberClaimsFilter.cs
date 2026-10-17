namespace CloudHealthOffice.Portal.Services;

// ── Member-Linkage DTOs (consumed by MemberDetailsDialog) ────────────────────

/// <summary>
/// Filter the Claims tab can apply when calling
/// <see cref="IClaimsService.SearchClaimsByMemberAsync"/>. Mirrors the
/// claims-service v1 query string so it serializes 1:1 to the wire.
/// </summary>
public class MemberClaimsFilter
{
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    /// <summary>One of the ClaimStatus enum names (e.g., "Approved").</summary>
    public string? Status { get; set; }
    public string? ProviderNPI { get; set; }
    /// <summary>One of the ClaimType enum names (e.g., "Professional").</summary>
    public string? ClaimType { get; set; }
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
