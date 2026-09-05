using FhirService.Services.PayerToPayer;

namespace FhirService.Models.PayerToPayer;

/// <summary>
/// A Payer-to-Payer member-match request (CMS-0057-F P2P-04). A receiving payer
/// asks Cloud Health Office — a prior/other payer — to resolve the transitioning
/// member across payer contexts from the identity attributes it holds, and to
/// return the relevant coverage context. Shaped after the Da Vinci PDex / HRex
/// FHIR <c>Patient/$member-match</c> operation (MemberPatient demographics +
/// old-coverage context), reduced to the attributes this slice matches on.
///
/// Unlike the P2P-01 respond (<see cref="PayerToPayerExchangeRequest"/>), the
/// primary key here is NOT a known CHO member id: it is cross-payer identity
/// resolution from demographics and/or the member's identifier under the prior
/// payer. Matching is deterministic and fail-safe — it never returns data for an
/// ambiguous or mismatched identity.
/// </summary>
public sealed class MemberMatchRequest
{
    /// <summary>Tenant the match is scoped to (isolation boundary).</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Identifier of the receiving (requesting) payer — audit context.</summary>
    public string ReceivingPayerId { get; init; } = string.Empty;

    /// <summary>Who/what initiated the match — audit context.</summary>
    public string? InitiatedBy { get; init; }

    // ── Member identity attributes supplied by the receiving payer ──────────────
    public string? FamilyName { get; init; }
    public string? GivenName { get; init; }
    public string? BirthDate { get; init; }   // yyyy-MM-dd
    public string? Gender { get; init; }

    /// <summary>Member/subscriber id the member held under the prior payer — a strong identifier.</summary>
    public string? MemberId { get; init; }

    /// <summary>Social security number, if supplied — a strong identifier.</summary>
    public string? Ssn { get; init; }

    // Supporting demographics.
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }

    // ── Requested coverage context (optional) ───────────────────────────────────
    /// <summary>When set, prefer the coverage issued by this payer.</summary>
    public string? RequestedPayerId { get; init; }

    /// <summary>When set, prefer the coverage with this subscriber id.</summary>
    public string? RequestedSubscriberId { get; init; }

    /// <summary>
    /// Point in time the coverage context is requested "as of" (yyyy-MM-dd). Used
    /// to pick the coverage in force then when a member has concurrent/overlapping
    /// coverages. Defaults to the match date when absent.
    /// </summary>
    public string? AsOfDate { get; init; }
}

/// <summary>Terminal outcome of a member-match.</summary>
public enum MemberMatchOutcome
{
    /// <summary>Exactly one member resolved, with a single coverage context.</summary>
    Matched,

    /// <summary>No member resolved from the supplied identity.</summary>
    NoMatch,

    /// <summary>More than one member resolved — refused to avoid returning the wrong person.</summary>
    AmbiguousMatch,

    /// <summary>The request did not carry enough identifying information to match safely.</summary>
    InsufficientCriteria,

    /// <summary>The request targeted a tenant this instance does not serve.</summary>
    TenantMismatch,

    /// <summary>A single member resolved, but the coverage context could not be reduced to one.</summary>
    AmbiguousCoverage,
}

/// <summary>
/// The normalized identity criteria a match is evaluated against. Normalization
/// is applied once here (see <c>MemberIdentityNormalizer</c>) so the policy
/// compares canonical values, not raw input.
/// </summary>
public sealed class MemberMatchCriteria
{
    public string? FamilyName { get; init; }
    public string? GivenName { get; init; }
    public string? BirthDate { get; init; }
    public string? Gender { get; init; }
    public string? MemberId { get; init; }
    public string? Ssn { get; init; }
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }

    public string? RequestedPayerId { get; init; }
    public string? RequestedSubscriberId { get; init; }
    public string? AsOfDate { get; init; }

    /// <summary>True when a strong identifier is present.</summary>
    public bool HasStrongIdentifier =>
        !string.IsNullOrWhiteSpace(MemberId) || !string.IsNullOrWhiteSpace(Ssn);

    /// <summary>
    /// Anti-enumeration gate: a match may proceed only with a strong identifier
    /// (member/subscriber id or SSN) OR the demographic pair family name + birth
    /// date. A single weak attribute (only a last name, only a gender, only a
    /// postal code) is never enough to search on.
    /// </summary>
    public bool IsSufficient =>
        HasStrongIdentifier
        || (!string.IsNullOrWhiteSpace(FamilyName) && !string.IsNullOrWhiteSpace(BirthDate));

    public static MemberMatchCriteria From(MemberMatchRequest request) => new()
    {
        FamilyName = MemberIdentityNormalizer.Name(request.FamilyName),
        GivenName = MemberIdentityNormalizer.Name(request.GivenName),
        BirthDate = MemberIdentityNormalizer.BirthDate(request.BirthDate),
        Gender = MemberIdentityNormalizer.Gender(request.Gender),
        MemberId = MemberIdentityNormalizer.Identifier(request.MemberId),
        Ssn = MemberIdentityNormalizer.Identifier(request.Ssn),
        PostalCode = MemberIdentityNormalizer.PostalCode(request.PostalCode),
        Phone = MemberIdentityNormalizer.Phone(request.Phone),
        Email = MemberIdentityNormalizer.Email(request.Email),
        RequestedPayerId = MemberIdentityNormalizer.Identifier(request.RequestedPayerId),
        RequestedSubscriberId = MemberIdentityNormalizer.Identifier(request.RequestedSubscriberId),
        AsOfDate = MemberIdentityNormalizer.BirthDate(request.AsOfDate),
    };
}

/// <summary>
/// Result of a member-match. On <see cref="MemberMatchOutcome.Matched"/> it
/// carries the stable CHO member and the selected coverage context — enough for
/// the P2P-01 export path to consume without re-matching. All other outcomes
/// carry no member/coverage data.
/// </summary>
public sealed class MemberMatchResult
{
    public MemberMatchOutcome Outcome { get; init; }

    /// <summary>The resolved CHO member (only when <see cref="Outcome"/> is Matched).</summary>
    public ChoMember? Member { get; init; }

    /// <summary>The selected coverage context (only when Matched and a coverage exists).</summary>
    public ChoCoverage? Coverage { get; init; }

    public string? MatchedMemberId => Member?.MemberId;

    public MemberMatchAuditEntry Audit { get; init; } = new();

    public bool Succeeded => Outcome == MemberMatchOutcome.Matched;

    public static MemberMatchResult Matched(ChoMember member, ChoCoverage? coverage, MemberMatchAuditEntry audit) =>
        new() { Outcome = MemberMatchOutcome.Matched, Member = member, Coverage = coverage, Audit = audit };

    public static MemberMatchResult Failure(MemberMatchOutcome outcome, MemberMatchAuditEntry audit) =>
        new() { Outcome = outcome, Audit = audit };
}

/// <summary>
/// Auditable record of a member-match — who asked, the tenant, the resolved
/// member/coverage ids, the outcome, and when. Carries NO raw identity
/// demographics (no name, DOB, address) so the match is traceable without
/// writing member PII into the audit trail.
/// </summary>
public sealed class MemberMatchAuditEntry
{
    public string TenantId { get; init; } = string.Empty;
    public string ReceivingPayerId { get; init; } = string.Empty;
    public string? InitiatedBy { get; init; }
    public string? MatchedMemberId { get; init; }
    public string? SelectedCoverageId { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}
