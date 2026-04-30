namespace ClaimsService.Models;

/// <summary>
/// Vendor-neutral request envelope passed to <see cref="Adapters.IClaimAdapter"/>
/// read methods. A single shape covers <c>GetClaimAsync</c>,
/// <c>GetClaimByNumberAsync</c>, <c>GetClaimVersionAsync</c>, and
/// <c>ListClaimVersionsAsync</c>; per-method required fields are documented on
/// the individual properties below. Search and submission have their own
/// dedicated request types.
/// </summary>
public class ClaimAdapterRequest
{
    /// <summary>Tenant id resolved by the request middleware. Required by all methods.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Per-version document id (<see cref="Claim.Id"/>). Required by
    /// <c>GetClaimAsync</c> when <see cref="ClaimVersionId"/> is not set;
    /// ignored otherwise.
    /// </summary>
    public string? ClaimId { get; set; }

    /// <summary>
    /// Payer-assigned claim number (<see cref="Claim.ClaimNumber"/>). Required
    /// by <c>GetClaimByNumberAsync</c>; ignored otherwise.
    /// </summary>
    public string? ClaimNumber { get; set; }

    /// <summary>
    /// Stable per-chain identifier (<see cref="Claim.ClaimVersionId"/>).
    /// Optional on <c>GetClaimAsync</c> — when set, the CHO adapter calls
    /// <c>GetLatestVersionAsync(ClaimVersionId, AsOf ?? UtcNow)</c> instead of
    /// the per-document-id read. Required by <c>GetClaimVersionAsync</c> and
    /// <c>ListClaimVersionsAsync</c>.
    /// </summary>
    public string? ClaimVersionId { get; set; }

    /// <summary>
    /// Specific version document id within a chain. Required by
    /// <c>GetClaimVersionAsync</c>; ignored otherwise.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// Effective date used to resolve which version of a chain applies.
    /// Optional on <c>GetClaimAsync</c> with a <see cref="ClaimVersionId"/>;
    /// when null, callers and adapter implementations should treat it as
    /// <see cref="DateTime.UtcNow"/> at call time.
    /// </summary>
    public DateTime? AsOf { get; set; }

    /// <summary>
    /// Page size for paged results — used by <c>ListClaimVersionsAsync</c>.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Continuation token for paged results — used by
    /// <c>ListClaimVersionsAsync</c>. Null requests the first page.
    /// </summary>
    public string? ContinuationToken { get; set; }

    /// <summary>
    /// Platform-specific configuration sourced from
    /// <c>configuration.claimsPlatform.platformSettings</c> on the tenant
    /// document (e.g. QNXT base URL, Facets credential reference). Adapters
    /// read what they need; the factory passes the value through unchanged.
    /// </summary>
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Vendor-neutral request envelope for <see cref="Adapters.IClaimAdapter.SearchClaimsAsync"/>.
/// Mirrors the canonical filter set on <c>IClaimRepository.SearchAsync</c>.
/// </summary>
public class ClaimSearchAdapterRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? ProviderNPI { get; set; }
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public ClaimStatus? Status { get; set; }
    public LineOfBusiness? LineOfBusiness { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Vendor-neutral request envelope for
/// <see cref="Adapters.IClaimAdapter.SearchClaimsForMemberAsync"/>. Mirrors the
/// portal Member Details dialog filter set on
/// <c>IClaimRepository.SearchForMemberAsync</c> (member id required; amount
/// range and claim type filters added).
/// </summary>
public class ClaimMemberSearchAdapterRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public ClaimStatus? Status { get; set; }
    public string? ProviderNPI { get; set; }
    public ClaimType? ClaimType { get; set; }
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Vendor-neutral request envelope for <see cref="Adapters.IClaimAdapter.SubmitClaimAsync"/>.
/// Carries the canonical <see cref="AdapterClaim"/> rather than the domain
/// type so vendor adapters can populate from their own payloads without
/// taking a hard dependency on the CHO domain model.
/// </summary>
public class ClaimSubmissionAdapterRequest
{
    public string TenantId { get; set; } = string.Empty;
    public AdapterClaim Claim { get; set; } = new();
    public string? CorrelationId { get; set; }
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}
