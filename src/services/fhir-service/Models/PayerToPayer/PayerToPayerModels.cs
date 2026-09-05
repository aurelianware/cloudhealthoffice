namespace FhirService.Models.PayerToPayer;

/// <summary>
/// An inbound Payer-to-Payer exchange request (CMS-0057-F P2P-01): a receiving
/// (new) payer asks Cloud Health Office — the prior payer — to respond with the
/// transitioning member's data. Carries the member identifiers the receiving
/// payer holds and the exchange context.
///
/// The member's opt-in authorization is deliberately NOT carried here: it is
/// decided server-side by <c>IPayerToPayerConsentGate</c> from the plan's own
/// consent state, so a receiving payer cannot self-attest consent. That gate
/// uses the generic active opt-in signal and does not introduce a dedicated
/// Payer-to-Payer ConsentType — P2P-03 stays PARTIAL and independent.
/// </summary>
public sealed class PayerToPayerExchangeRequest
{
    /// <summary>Tenant the exchange is scoped to (isolation boundary).</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Identifier of the receiving (requesting) payer — audit context.</summary>
    public string ReceivingPayerId { get; init; } = string.Empty;

    /// <summary>Who/what initiated the exchange — audit context.</summary>
    public string? InitiatedBy { get; init; }

    // ── Member identifiers supplied by the receiving payer ──────────────────────
    /// <summary>Prior-payer member / subscriber id — the primary match key.</summary>
    public string? MemberId { get; init; }
    public string? LastName { get; init; }
    public string? Dob { get; init; }     // yyyy-MM-dd
    public string? Gender { get; init; }

    /// <summary>Exchange date; anchors the lookback window.</summary>
    public DateTime ExchangeDateUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Claims lookback for included data (locked P2P rule: 5 years).</summary>
    public int LookbackYears { get; init; } = 5;
}

/// <summary>Terminal outcome of an inbound P2P respond.</summary>
public enum PayerToPayerOutcome
{
    /// <summary>Member matched, authorized, and a data package was produced.</summary>
    Exported,

    /// <summary>No member matched the supplied identifiers.</summary>
    NoMatch,

    /// <summary>More than one member matched — refused to avoid returning the wrong member.</summary>
    AmbiguousMatch,

    /// <summary>The request did not carry enough identifying information to match safely.</summary>
    InsufficientCriteria,

    /// <summary>The member has no active opt-in consent for the exchange.</summary>
    NotAuthorized,

    /// <summary>The request targeted a tenant this instance does not serve.</summary>
    TenantMismatch,
}

/// <summary>Result of an inbound P2P respond, including an audit entry.</summary>
public sealed class PayerToPayerExportResult
{
    public PayerToPayerOutcome Outcome { get; init; }
    public string? MatchedMemberId { get; init; }

    /// <summary>The member-scoped FHIR export package (only when <see cref="Outcome"/> is Exported).</summary>
    public FhirBundle? Bundle { get; init; }

    public PayerToPayerAuditEntry Audit { get; init; } = new();

    public bool Succeeded => Outcome == PayerToPayerOutcome.Exported;
}

/// <summary>
/// Auditable record of an exchange — who/what, which member, which receiving
/// payer, when, and the result. Carries only the matched member id (no broader
/// PHI) so the exchange is traceable without leaking clinical detail into logs.
/// </summary>
public sealed class PayerToPayerAuditEntry
{
    public string TenantId { get; init; } = string.Empty;
    public string ReceivingPayerId { get; init; } = string.Empty;
    public string? InitiatedBy { get; init; }
    public string? MatchedMemberId { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Number of FHIR resources included in the export (0 when not exported).</summary>
    public int ResourceCount { get; init; }
}

/// <summary>
/// The identifying criteria used to resolve a member for an inbound respond.
/// P2P-01 matches by prior-payer member id (with any supplied demographics
/// confirmed). Demographic-only cross-payer identity resolution is the FHIR
/// <c>$member-match</c> operation — that is P2P-04 and is deliberately not
/// implemented here.
/// </summary>
public sealed class PayerToPayerMemberCriteria
{
    public string? MemberId { get; init; }
    public string? LastName { get; init; }
    public string? Dob { get; init; }
    public string? Gender { get; init; }

    /// <summary>P2P-01 requires a member id to match safely.</summary>
    public bool IsSufficient => !string.IsNullOrWhiteSpace(MemberId);

    public static PayerToPayerMemberCriteria From(PayerToPayerExchangeRequest request) => new()
    {
        MemberId = request.MemberId,
        LastName = request.LastName,
        Dob = request.Dob,
        Gender = request.Gender,
    };
}
