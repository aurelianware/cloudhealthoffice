namespace CloudHealthOffice.Events;

/// <summary>
/// Service Bus payload for the <c>ai-examination-events</c> topic, emitted by
/// claims-examiner-service after it writes a structured AI examination back
/// to claims-service. Capability 5.9.
///
/// <para>
/// Notification-only contract. Carries enough discriminator for downstream
/// routing (5.10 remittance generation acts on the recommended disposition)
/// without duplicating the full <c>AiExamination</c> record. Consumers that
/// need <c>Rationale</c>, <c>PolicyCitations</c>, <c>ModelId</c>, or
/// <c>PromptVersion</c> fetch the full record via
/// <c>GET /api/claims/{id}</c>. Keeping the event minimal makes it cheap to
/// dedup at the broker (Service Bus <c>MessageId</c>) and forward-compatible
/// — adding fields is non-breaking; trimming them isn't.
/// </para>
///
/// <para>
/// The event is terminal for the AI examination cycle (Decision 3): no
/// pipeline re-entry, no expectation that downstream consumers call back
/// into the examiner. One logical event per claim — re-emissions within
/// the Service Bus dedup window are dropped by the broker keyed on
/// <c>MessageId = "ai-completed:{claimId}"</c> (Decision 15).
/// </para>
/// </summary>
public class ClaimAiExaminationCompletedEvent
{
    public string ClaimId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// One of <c>Approve</c>, <c>Deny</c>, <c>RequestInfo</c>, or
    /// <c>EscalateToHuman</c>. The fallback path emits
    /// <c>EscalateToHuman</c>; downstream consumers should treat all four
    /// as valid terminal recommendations.
    /// </summary>
    public string RecommendedDisposition { get; set; } = string.Empty;

    /// <summary>0–1 inclusive. Defensive clamp upstream.</summary>
    public double ConfidenceScore { get; set; }

    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Activity / X-Correlation-Id propagated from the original
    /// <c>ClaimPendedEvent</c> when available; null otherwise.
    /// </summary>
    public string? CorrelationId { get; set; }
}
