namespace FhirService.Services.Clinical;

/// <summary>Where a stored clinical resource came from.</summary>
public enum ClinicalResourceOrigin
{
    /// <summary>
    /// Received from another payer through a Payer-to-Payer exchange. It is NOT
    /// CHO's authoritative clinical record and must never be presented as one.
    /// </summary>
    Imported,

    /// <summary>
    /// Authored inside Cloud Health Office. Reserved: no CHO component writes
    /// native clinical data today (see docs/architecture/clinical-fhir.md), and
    /// the axis exists so that when one does it coexists with imported data
    /// instead of overwriting it.
    /// </summary>
    ChoNative,
}

/// <summary>
/// One clinical resource as the durable store holds it: the served FHIR payload
/// plus every fact needed to bind, attribute and version it.
///
/// The tenant and member here are CHO's own — established by the trusted
/// exchange context at ingestion, never lifted out of the payload — so this
/// record, not the resource's own <c>subject</c>, is what the read path
/// authorizes against.
/// </summary>
public sealed record StoredClinicalResource
{
    /// <summary>Owning tenant. Present in every store query, never inferred.</summary>
    public required string TenantId { get; init; }

    /// <summary>The CHO member the resource is filed under.</summary>
    public required string MemberId { get; init; }

    public required string ResourceType { get; init; }

    /// <summary>The logical id CHO serves it under (see <see cref="ClinicalResourceIdentity"/>).</summary>
    public required string ClinicalId { get; init; }

    /// <summary>The stored FHIR JSON, as validated at ingestion.</summary>
    public required string ResourceJson { get; init; }

    public ClinicalResourceOrigin Origin { get; init; } = ClinicalResourceOrigin.Imported;

    /// <summary>The payer the resource came from. Null for CHO-native data.</summary>
    public string? SourcePayerId { get; init; }

    /// <summary>The resource's id at its source. Null for CHO-native data.</summary>
    public string? SourceResourceId { get; init; }

    /// <summary>The exchange that delivered this version, for traceability.</summary>
    public string? ExchangeId { get; init; }

    /// <summary>SHA-256 of the stored payload — the version discriminator.</summary>
    public required string ContentHash { get; init; }

    /// <summary>When this version became CHO's record of the resource.</summary>
    public DateTime LastUpdatedUtc { get; init; }
}

/// <summary>
/// A member-scoped clinical search. Tenant, member and resource type are all
/// REQUIRED and all applied by the storage query — never by filtering a wider
/// result set in application code, which is how a cross-member or cross-tenant
/// row reaches memory in the first place.
/// </summary>
public sealed record ClinicalResourceQuery
{
    public required string TenantId { get; init; }
    public required string MemberId { get; init; }
    public required string ResourceType { get; init; }

    /// <summary>FHIR <c>_id</c>, when the caller narrowed the search to one resource.</summary>
    public string? ClinicalId { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size.</summary>
    public int Count { get; init; } = 20;
}

/// <summary>One page of clinical resources plus the total across all pages.</summary>
public sealed record ClinicalResourcePage(IReadOnlyList<StoredClinicalResource> Items, int Total);

/// <summary>
/// The durable, member-scoped clinical FHIR store behind Patient and Provider
/// Access.
///
/// OWNERSHIP. This is a READ contract over data another pipeline commits, and
/// there is deliberately only one writer: Payer-to-Payer ingestion
/// (<c>PayerToPayerPackageIngestionService</c>). Clinical rows are the same rows
/// that ingestion stages and commits — the import store PROMOTED into a serving
/// store, not a copy of it — so there is no projection to fall behind, no second
/// place a resource could be stale in, and no dual-write to reconcile. The
/// implementations are the same two classes: the MongoDB store when
/// <c>MongoDb:ConnectionString</c> is configured, the in-process one otherwise.
///
/// FRESHNESS. Reads return the version from the most recently COMMITTED
/// exchange for each identity. A package that is staged but not committed is
/// invisible and never displaces a committed version, so a partial or failed
/// ingestion cannot be read as the member's clinical record.
/// </summary>
public interface IClinicalResourceStore
{
    /// <summary>
    /// One resource, by CHO logical id, WITHIN a member. The member is part of
    /// the query rather than a check applied afterwards: an id belonging to
    /// another member or another tenant simply does not resolve, so a direct
    /// read cannot return a row the caller was not authorized for even for an
    /// instant.
    /// </summary>
    Task<StoredClinicalResource?> GetAsync(
        string tenantId, string memberId, string resourceType, string clinicalId, CancellationToken ct = default);

    /// <summary>
    /// The member's resources of one type, newest first, paged. Every
    /// constraint in <see cref="ClinicalResourceQuery"/> is applied by the store.
    /// </summary>
    Task<ClinicalResourcePage> SearchAsync(ClinicalResourceQuery query, CancellationToken ct = default);

    /// <summary>
    /// The resource type behind each of the given local identities, for the
    /// references CHO normalized at ingestion. Scoped to the tenant and member,
    /// so resolving a reference can never confirm the existence of another
    /// member's resource. Unknown identities are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetResourceTypesAsync(
        string tenantId, string memberId, IReadOnlyCollection<string> localIds, CancellationToken ct = default);
}
