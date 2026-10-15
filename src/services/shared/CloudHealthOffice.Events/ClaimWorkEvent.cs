namespace CloudHealthOffice.Events;

/// <summary>
/// Kafka topic and event types for claim work: claims-service tells
/// work-management-service that a claim needs an examiner (it was pended, or
/// re-pended with different pends) or no longer does (it left Pended: resolved,
/// overridden, re-adjudicated to Pay / Deny, voided, finalized).
/// </summary>
public static class ClaimWorkEventTopics
{
    /// <summary>
    /// One topic for both kinds, keyed by claim id, so the two kinds for one
    /// claim keep their order within a partition. The version suffix follows
    /// the claims.pended.v1 convention: a breaking schema change is a new topic.
    /// </summary>
    public const string Topic = "claims.work.v1";

    /// <summary>The claim is Pended: an examiner must work it.</summary>
    public const string WorkNeeded = "claim.work.needed";

    /// <summary>The claim left Pended: any open work item for it is complete.</summary>
    public const string WorkResolved = "claim.work.resolved";
}

/// <summary>
/// Payload of <see cref="ClaimWorkEventTopics.Topic"/>. Carries the attributes
/// work routing and prioritization need, so the consumer never calls back to
/// claims-service for an ordinary event (the reconcile path does, to repair
/// drift). Minimum necessary: no member name, no diagnosis, no service lines.
///
/// <para><b>Ordering and idempotency.</b> <see cref="SourceVersion"/> is the
/// UTC tick count at which claims-service observed the transition. A consumer
/// applies an event only when its SourceVersion is newer than the last one it
/// applied for the claim, so a redelivered event (same version) and a late
/// event (older version) are both no-ops.</para>
/// </summary>
public class ClaimWorkEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary><see cref="ClaimWorkEventTopics.WorkNeeded"/> or <see cref="ClaimWorkEventTopics.WorkResolved"/>.</summary>
    public string EventType { get; set; } = ClaimWorkEventTopics.WorkNeeded;

    public string EventVersion { get; set; } = "1";

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Monotonic per claim (UTC ticks when the transition was observed).</summary>
    public long SourceVersion { get; set; }

    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;

    /// <summary>The claim status after the transition (Pended for a work-needed event).</summary>
    public string ClaimStatus { get; set; } = string.Empty;

    /// <summary>
    /// For a work-resolved event: what resolved it (the disposition, "Voided",
    /// "Paid", "Reprocessed", ...). Null for work-needed.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>Who caused the transition (examiner, supervisor, or the service).</summary>
    public string? ActorId { get; set; }

    // ── Routing attributes (snapshot at the transition) ─────────────────

    /// <summary>The routing pend code (<c>PendDetails.PendCode</c>).</summary>
    public string? PrimaryPendCode { get; set; }

    /// <summary>Every pend code on the claim, the routing one first.</summary>
    public List<string> PendCodes { get; set; } = new();

    public string? PendReason { get; set; }

    /// <summary>Fingerprint of the pends (<c>PendDetails.Fingerprint</c>); changes when the claim re-pends differently.</summary>
    public string? PendFingerprint { get; set; }

    public DateTime? PendedAt { get; set; }

    /// <summary>Line of business as a string (e.g. "Commercial", "Medicaid").</summary>
    public string? LineOfBusiness { get; set; }

    /// <summary>Claim type as a string (Professional, Institutional, Dental, ...).</summary>
    public string? ClaimType { get; set; }

    public decimal TotalChargeAmount { get; set; }

    public string? BillingProviderNpi { get; set; }

    /// <summary>Billing provider TIN, when claims-service has one (not on every claim today).</summary>
    public string? BillingProviderTin { get; set; }

    public string? BillingProviderName { get; set; }

    /// <summary>Network tier from the adjudication result (e.g. "InNetwork", "OutOfNetwork"), when priced.</summary>
    public string? NetworkTier { get; set; }

    /// <summary>
    /// Expedited handling: an institutional emergency / urgent admission
    /// (admission type 1 or 2). Routing and priority can weigh it.
    /// </summary>
    public bool Expedited { get; set; }

    /// <summary>When the payer received the claim (the clock prompt-pay deadlines run from).</summary>
    public DateTime? ReceivedDate { get; set; }

    public DateTime? ServiceDateFrom { get; set; }

    public string? MemberId { get; set; }
}
