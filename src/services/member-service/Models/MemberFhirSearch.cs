using System.Text.Json.Nodes;

namespace MemberService.Models;

/// <summary>
/// Structured query behind <c>GET /api/v1/members/fhir/Patient</c>. FHIR search
/// syntax (modifiers, date prefixes, token systems, paging) is parsed by
/// fhir-service; this service receives plain, already-resolved filters
/// (ADR 018). Every filter is optional and they combine with AND.
/// </summary>
public sealed class MemberFhirSearchQuery
{
    /// <summary>Largest page the endpoint returns.</summary>
    public const int MaxLimit = 100;

    /// <summary>Largest offset accepted, bounding skip cost on large tenants.</summary>
    public const int MaxOffset = 10_000;

    /// <summary>Exact member id (FHIR <c>_id</c> or a member-id identifier).</summary>
    public string? MemberId { get; init; }

    /// <summary>
    /// Members whose subscriber is this member id (the subscriber themself is
    /// not included unless their own record names them). Payer-to-Payer member
    /// match uses it to find the dependents who carry a subscriber's id (ADR 020).
    /// </summary>
    public string? SubscriberMemberId { get; init; }

    /// <summary>
    /// Match <see cref="MemberId"/> and <see cref="SubscriberMemberId"/> in any
    /// form that normalizes to the same identifier: ignoring case, spaces and
    /// hyphens, as Payer-to-Payer member match compares them (ADR 020). Off, the
    /// match is exact.
    /// </summary>
    public bool AnyIdentifierForm { get; init; }

    /// <summary>Case-insensitive prefix of the family name.</summary>
    public string? Family { get; init; }

    /// <summary>Case-insensitive prefix of the first or middle name.</summary>
    public string? Given { get; init; }

    /// <summary>Case-insensitive prefix of the family, first or middle name.</summary>
    public string? Name { get; init; }

    /// <summary>Inclusive lower bound on the date of birth (date part only).</summary>
    public DateTime? BirthDateFrom { get; init; }

    /// <summary>Exclusive upper bound on the date of birth (date part only).</summary>
    public DateTime? BirthDateTo { get; init; }

    /// <summary>FHIR administrative gender: male, female, other or unknown.</summary>
    public string? Gender { get; init; }

    /// <summary>Inclusive lower bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedFrom { get; init; }

    /// <summary>Exclusive upper bound on the record's last update (UTC).</summary>
    public DateTime? LastUpdatedTo { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>
/// One search result: the member id, the projected Patient, and the member's
/// subscriber, which a Patient does not carry. It is null for a subscriber (a
/// subscriber has no subscriber of their own) and for a member with none recorded.
/// <see cref="RelationshipCode"/> is the member's relationship to that subscriber
/// as the 834 sent it (INS02: 18 self, 01 spouse, 19 child, ...): 18 for a
/// subscriber, the recorded code for a dependent, null when none was recorded.
/// fhir-service reads both for Coverage.subscriberId and Coverage.relationship.
/// </summary>
public sealed record MemberFhirSearchEntry(
    string MemberId, JsonObject Resource, string? SubscriberMemberId = null, string? RelationshipCode = null);

/// <summary>
/// A page of projected Patients. <see cref="Items"/> carries <c>MemberId</c> on
/// each entry so the record-access audit logs which members were returned.
/// </summary>
/// <summary>
/// A page of results. <see cref="AnyIdentifierForm"/> echoes an any-form request,
/// so a caller can tell this service applied it rather than an older one that
/// ignored the parameter and matched exactly.
/// </summary>
public sealed record MemberFhirSearchResponse(long Total, IReadOnlyList<MemberFhirSearchEntry> Items, bool? AnyIdentifierForm = null);

/// <summary>The FHIR administrative-gender codes this search understands.</summary>
public static class FhirGenderCodes
{
    public const string Male = "male";
    public const string Female = "female";
    public const string Other = "other";
    public const string Unknown = "unknown";

    public static bool IsValid(string? code) =>
        code is Male or Female or Other or Unknown;
}

/// <summary>
/// The gender codes CHO stores on a <see cref="Member"/>, as X12 834 DMG03 carries
/// them.
///
/// <para>
/// These are the codes the FHIR projection translates from, and the only ones it
/// recognises. Nothing enforced them on the way in, so a value such as
/// <c>"X"</c>, <c>"Male"</c> or a typo was accepted, stored, and then projected
/// as US Core <c>gender: "unknown"</c> — an assertion CHO never had grounds to
/// make, and indistinguishable from a member who genuinely declined to state.
/// US Core requires <c>Patient.gender</c>, so the projection cannot omit the
/// element to signal "CHO does not know what this value means"; the only place
/// to catch it is the request.
/// </para>
/// </summary>
public static class StoredGenderCodes
{
    public const string Male = "M";
    public const string Female = "F";
    public const string Unknown = "U";

    /// <summary>
    /// For <c>[RegularExpression]</c> on a request model, which needs a compile-time
    /// constant. Case-insensitive, because the projection upper-cases before mapping.
    ///
    /// <para>
    /// The empty string is allowed alongside null: both mean "not stated", which is
    /// what the projection's <c>unknown</c> honestly reflects. An unrecognised
    /// <em>code</em> is the thing being refused, not an absent one — and the 834
    /// tokenizer already returns null for an empty DMG03, so an import is unaffected.
    /// </para>
    /// </summary>
    public const string Pattern = "^([MFUmfu])?$";

    public const string ValidationMessage =
        "Gender must be M, F or U (X12 834 DMG03), or omitted. An unrecognised value "
        + "cannot be projected to FHIR without asserting a gender CHO was not told.";

    public static bool IsValid(string? code) =>
        code is not null && code.Length == 1
        && (code is Male or Female or Unknown
            || code.ToUpperInvariant() is Male or Female or Unknown);
}
