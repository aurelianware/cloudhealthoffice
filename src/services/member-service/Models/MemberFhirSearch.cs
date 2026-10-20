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

/// <summary>One projected Patient and the member it was projected from.</summary>
/// <summary>
/// One search result: the member id, the projected Patient, and the member's
/// subscriber (null for a member with none recorded), which a Patient does not carry.
/// </summary>
public sealed record MemberFhirSearchEntry(string MemberId, JsonObject Resource, string? SubscriberMemberId = null);

/// <summary>
/// A page of projected Patients. <see cref="Items"/> carries <c>MemberId</c> on
/// each entry so the record-access audit logs which members were returned.
/// </summary>
public sealed record MemberFhirSearchResponse(long Total, IReadOnlyList<MemberFhirSearchEntry> Items);

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
