using System.Text.Json.Nodes;

namespace ClaimsService.Models;

/// <summary>
/// Structured query behind <c>GET /fhir/Claim</c>. FHIR search syntax
/// (modifiers, date prefixes, token systems, paging) is parsed by fhir-service;
/// this service receives plain, already-resolved filters (ADR 018, extended to
/// Claim by ADR 024). Every filter is optional and they combine with AND.
/// </summary>
public sealed class ClaimFhirSearchQuery
{
    /// <summary>Largest page the endpoint returns.</summary>
    public const int MaxLimit = 100;

    /// <summary>Largest offset accepted, bounding skip cost on large tenants.</summary>
    public const int MaxOffset = 10_000;

    /// <summary>Exact chain-stable claim id (FHIR <c>_id</c>, the ClaimVersionId).</summary>
    public string? ClaimVersionId { get; init; }

    /// <summary>The member the claim is about (FHIR <c>patient</c>).</summary>
    public string? MemberId { get; init; }

    /// <summary>Inclusive lower bound on submission (FHIR <c>created</c>).</summary>
    public DateTime? CreatedFrom { get; init; }

    /// <summary>Exclusive upper bound on submission (FHIR <c>created</c>).</summary>
    public DateTime? CreatedTo { get; init; }

    /// <summary>
    /// FHIR Claim status, as the projection emits it: <c>active</c> or
    /// <c>cancelled</c>. Any other code matches nothing, because no stored
    /// record projects as one.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>CHO claim type (professional, institutional, dental).</summary>
    public ClaimType? ClaimType { get; init; }

    /// <summary>
    /// Any of these claim types (the ExplanationOfBenefit <c>type</c> search,
    /// which can name more than one). Combines with <see cref="ClaimType"/> by
    /// AND; null or empty does not filter.
    /// </summary>
    public IReadOnlyCollection<ClaimType>? ClaimTypes { get; init; }

    /// <summary>Exact claim number, the EOB's unique-claim-id identifier.</summary>
    public string? ClaimNumber { get; init; }

    /// <summary>
    /// Inclusive lower bound of a service window the claim's service dates must
    /// overlap (the EOB <c>service-date</c> search over <c>billablePeriod</c>).
    /// Service dates are whole days, so a claim whose last service day is the
    /// day this bound falls in overlaps it.
    /// </summary>
    public DateTime? ServiceFrom { get; init; }

    /// <summary>Exclusive upper bound of that window: the first service day must start before it.</summary>
    public DateTime? ServiceTo { get; init; }

    /// <summary>
    /// Inclusive lower bound on the first day of service (<c>billablePeriod.start</c>):
    /// the day matches when any part of it is at or after this bound.
    /// </summary>
    public DateTime? ServiceStartFrom { get; init; }

    /// <summary>Exclusive upper bound on the first day of service: the day must start before it.</summary>
    public DateTime? ServiceStartTo { get; init; }

    /// <summary>Inclusive lower bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedFrom { get; init; }

    /// <summary>Exclusive upper bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedTo { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>
/// One search result: the chain-stable claim id, the member it is about, and
/// the projected FHIR Claim. <c>MemberId</c> is carried so the claim-access
/// audit logs whose claims were returned.
/// </summary>
public sealed record ClaimFhirSearchEntry(string ClaimVersionId, string MemberId, JsonObject Resource);

/// <summary>
/// A page of projected Claims. The shape — <c>total</c> plus
/// <c>items[].resource</c> — is the one fhir-service's CHO-native adapter
/// reads, shared with member-service, coverage-service and encounter-service.
/// </summary>
public sealed record ClaimFhirSearchResponse(long Total, IReadOnlyList<ClaimFhirSearchEntry> Items);

/// <summary>The FHIR Claim status codes this projection can emit.</summary>
public static class FhirClaimStatusCodes
{
    /// <summary>The claim stands, whatever adjudication has decided about it.</summary>
    public const string Active = "active";

    /// <summary>The claim was voided.</summary>
    public const string Cancelled = "cancelled";

    public static bool IsEmitted(string? code) => code is Active or Cancelled;
}
