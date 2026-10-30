using System.Text.Json.Nodes;

namespace EncounterService.Models;

/// <summary>
/// Structured query behind <c>GET /api/v1/encounters/fhir/Encounter</c>. FHIR
/// search syntax (modifiers, date prefixes, token systems, paging) is parsed by
/// fhir-service; this service receives plain, already-resolved filters
/// (ADR 018, extended to Encounter by ADR 024). Every filter is optional and
/// they combine with AND.
/// </summary>
public sealed class EncounterFhirSearchQuery
{
    /// <summary>Largest page the endpoint returns.</summary>
    public const int MaxLimit = 100;

    /// <summary>Largest offset accepted, bounding skip cost on large tenants.</summary>
    public const int MaxOffset = 10_000;

    /// <summary>Exact encounter id (FHIR <c>_id</c>).</summary>
    public string? EncounterId { get; init; }

    /// <summary>The member the encounter is about (FHIR <c>patient</c>/<c>subject</c>).</summary>
    public string? MemberId { get; init; }

    /// <summary>Inclusive lower bound on the service period's end, so an encounter overlapping the window matches.</summary>
    public DateTime? ServiceDateFrom { get; init; }

    /// <summary>Exclusive upper bound on the service period's start.</summary>
    public DateTime? ServiceDateTo { get; init; }

    /// <summary>
    /// FHIR Encounter status, as the projection emits it: <c>finished</c> or
    /// <c>entered-in-error</c>. Any other code matches nothing, because no
    /// stored record projects as one.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>CHO encounter type (professional, institutional, dental).</summary>
    public EncounterType? EncounterType { get; init; }

    /// <summary>The submitter's encounter control number (FHIR <c>identifier</c>).</summary>
    public string? ControlNumber { get; init; }

    /// <summary>
    /// v3-ActCode codes (FHIR <c>class</c>), any of which matches. The class is
    /// derived from the place of service, so these are matched through it; a
    /// code the projection never emits matches nothing.
    /// </summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Inclusive lower bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedFrom { get; init; }

    /// <summary>Exclusive upper bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedTo { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>
/// One search result: the encounter id, the member it is about, and the
/// projected FHIR Encounter. <c>MemberId</c> is carried so the record-access
/// audit logs whose encounters were returned.
/// </summary>
public sealed record EncounterFhirSearchEntry(string EncounterId, string MemberId, JsonObject Resource);

/// <summary>
/// A page of projected Encounters. The shape — <c>total</c> plus
/// <c>items[].resource</c> — is the one fhir-service's CHO-native adapter
/// reads, shared with member-service and coverage-service.
/// </summary>
public sealed record EncounterFhirSearchResponse(long Total, IReadOnlyList<EncounterFhirSearchEntry> Items);

/// <summary>The FHIR Encounter status codes this projection can emit.</summary>
public static class FhirEncounterStatusCodes
{
    /// <summary>The service was rendered: every encounter CHO holds records a completed contact.</summary>
    public const string Finished = "finished";

    /// <summary>The submitter voided the encounter, so the contact it asserted is withdrawn.</summary>
    public const string EnteredInError = "entered-in-error";

    public static bool IsEmitted(string? code) => code is Finished or EnteredInError;
}
