namespace CloudHealthOffice.Events;

/// <summary>
/// Event payload for claims.pended.v1. Shared contract between claims-service
/// (producer) and claims-examiner-service (consumer). The version suffix on the
/// topic name is intentional — schema changes get a new topic, never an in-place
/// break. This type lives in a shared library so producer and consumer cannot
/// drift out of sync; any change here is a change both sides see at build time.
/// </summary>
public class ClaimPendedEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "claim.pended";
    public string EventVersion { get; set; } = "1";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Line of business as a string (e.g. "Commercial", "Medicare"). String is
    /// used on the wire rather than an enum so the contract does not depend on
    /// a shared enum definition; consumers parse defensively.
    /// </summary>
    public string LineOfBusiness { get; set; } = string.Empty;

    public decimal TotalChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }

    /// <summary>
    /// Pend reason details copied from Claim.PendDetails so consumers can decide
    /// whether to act without an extra round-trip back to claims-service. Consumers
    /// that need the full claim still fetch it via GET /api/claims/{id}.
    /// </summary>
    public PendDetails? PendDetails { get; set; }
}

/// <summary>
/// Pend reason carried on the event. Written by the adjudication workflow at
/// the moment of the pend; never mutated by downstream consumers.
/// </summary>
public class PendDetails
{
    /// <summary>
    /// Short pend reason code consumed by the work queue categorizer.
    /// Recognized values: NCCI, MUE, AUTH, NOAUTH, OON, NOCONTRACT, COB, MEDREVIEW, CLINICAL.
    /// </summary>
    public string PendCode { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the pend reason.
    /// </summary>
    public string? PendReason { get; set; }

    /// <summary>
    /// UTC timestamp when the claim was pended.
    /// </summary>
    public DateTime PendedAt { get; set; }

    /// <summary>
    /// NCCI/MUE edit failures that caused the pend. Empty for non-edit pends.
    /// </summary>
    public List<NcciEditFailureSnapshot> EditFailures { get; set; } = new();
}

/// <summary>
/// NCCI/MUE edit failure as carried on the event. Mirrors the snapshot shape
/// claims-service stores on Claim.PendDetails.EditFailures, without the MongoDB
/// persistence attributes — this is the wire-contract copy.
/// </summary>
public class NcciEditFailureSnapshot
{
    /// <summary>NcciPair or Mue.</summary>
    public string EditType { get; set; } = string.Empty;

    /// <summary>NE001 (NCCI bundling) or NE002 (MUE).</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>Human-readable description of the failure.</summary>
    public string? Message { get; set; }

    /// <summary>Column 1 procedure code (NCCI pair edits only).</summary>
    public string? Column1Code { get; set; }

    /// <summary>Column 2 procedure code (NCCI pair edits only).</summary>
    public string? Column2Code { get; set; }

    /// <summary>Claim line numbers affected by the edit.</summary>
    public List<int> AffectedLineNumbers { get; set; } = new();

    /// <summary>
    /// True if a -59/X{EPSU} modifier was already present at submission. The AI examiner
    /// is only invoked for edits where this is the legal override path; see
    /// IsModifierAddressable() for the v1 selection rule.
    /// </summary>
    public bool ModifierOverridePresent { get; set; }

    /// <summary>For MUE failures: units billed.</summary>
    public decimal? UnitsBilled { get; set; }

    /// <summary>For MUE failures: MUE max units limit.</summary>
    public int? MueMaxUnits { get; set; }

    /// <summary>Suggested CARC for the EOB/835.</summary>
    public string? SuggestedCarc { get; set; }

    /// <summary>Suggested RARC remark code.</summary>
    public string? SuggestedRarc { get; set; }

    /// <summary>
    /// True when the edit type is one a -59/X{EPSU} modifier could legally override.
    /// v1 of the AI examiner only acts on NCCI pair edits with ModifierIndicator = 1,
    /// which the engine surfaces as RuleId NE001 with ModifierOverridePresent reflecting
    /// what the submitter sent. The examiner reviews whether a -59/X{EPSU} should have
    /// been billed; MUE/unit-limit edits are out of scope for v1.
    /// </summary>
    public bool IsModifierAddressable() =>
        string.Equals(EditType, "NcciPair", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RuleId, "NE001", StringComparison.OrdinalIgnoreCase);
}
