namespace ClaimsService.Models.Messaging;

/// <summary>
/// Service Bus payload contracts for the <c>claim-version-events</c> topic
/// (capability 5.5). The topic is the canonical claim-lifecycle notification
/// surface; future capabilities (5.10 remittance, 5.11 FHIR projection
/// rebuilder, 5.12 adjustment workflow) add their own subscriptions filtered
/// by the <c>MessageType</c> application property.
/// </summary>
internal static class ClaimVersionEventTopics
{
    /// <summary>Single broad topic for the claim version lifecycle (Decision 2).</summary>
    public const string TopicName = "claim-version-events";

    /// <summary>Subscription consumed by the adjudication orchestrator.</summary>
    public const string AdjudicationSubscriptionName = "adjudication-orchestrator";

    /// <summary>
    /// Service Bus application property used to discriminate message types.
    /// Filter rules on each subscription select the relevant subset; producer
    /// side sets this on every <c>SendOptions.Properties</c>.
    /// </summary>
    public const string MessageTypeProperty = "MessageType";
}

/// <summary>
/// Stable string values for the <c>MessageType</c> application property.
/// Adding new values requires a paired Bicep subscription/filter update if
/// any subscriber wants to consume them.
/// </summary>
internal static class ClaimVersionMessageTypes
{
    public const string Submitted = "ClaimVersionSubmitted";
    public const string Adjudicated = "ClaimVersionAdjudicated";
    public const string Reversed = "ClaimVersionReversed";
}

/// <summary>
/// Emitted by <see cref="Services.ClaimSubmissionService"/> after a
/// successful submission (capability 5.5 dual-emit modification).
/// Triggers the adjudication pipeline.
///
/// <para>
/// The payload deliberately carries identifiers, not the full claim — the
/// orchestrator re-fetches the canonical version row through
/// <c>IClaimAdapter.GetClaimVersionAsync</c> so the pipeline always
/// adjudicates the latest persisted state, not a stale snapshot.
/// </para>
/// </summary>
public class ClaimVersionSubmittedMessage
{
    public required string TenantId { get; init; }
    public required string ClaimId { get; init; }
    public required string ClaimVersionId { get; init; }
    public int VersionNumber { get; init; }

    /// <summary>Caller identity propagated through the audit chain.</summary>
    public string? ActorId { get; init; }

    /// <summary>Activity / X-Correlation-Id from the original submission.</summary>
    public string? CorrelationId { get; init; }

    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Emitted by the adjudication orchestrator after a pipeline run finishes,
/// regardless of pass / pend / reject / deny outcome. Future subscriptions
/// (5.10 remittance, 5.12 adjustments, ...) consume this stream.
/// </summary>
public class ClaimVersionAdjudicatedMessage
{
    public required string TenantId { get; init; }
    public required string ClaimId { get; init; }
    public required string ClaimVersionId { get; init; }
    public int VersionNumber { get; init; }

    /// <summary>Final terminal-or-pass outcome of the pipeline run.</summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Highest-precedence reason from any stage that did not pass — empty
    /// when the run completed clean.
    /// </summary>
    public string? Reason { get; init; }

    public string? CorrelationId { get; init; }

    public DateTimeOffset AdjudicatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Emitted by <see cref="Services.IClaimAdjustmentService"/> after the
/// predecessor version has been superseded as part of a 5.12 adjustment
/// workflow. Distinct from <c>ClaimVersionSuperseded</c>: this message
/// signals downstream consumers (audit/lineage, future FHIR _history,
/// payment-service ReversalRun queue) that the predecessor's accumulator
/// impact and provider payment must be reversed.
///
/// <para>
/// The new version's pipeline run emits a separate
/// <see cref="ClaimVersionSubmittedMessage"/> via the standard submission
/// path; this message is purely the supersession + reversal-intent signal.
/// </para>
/// </summary>
public class ClaimVersionReversedMessage
{
    public required string TenantId { get; init; }

    /// <summary>The predecessor claim row id (the version being reversed).</summary>
    public required string ClaimId { get; init; }

    /// <summary>Chain key — stable across all versions of the claim.</summary>
    public required string ClaimVersionId { get; init; }

    /// <summary>The predecessor's per-version VersionId.</summary>
    public required string PredecessorVersionId { get; init; }

    /// <summary>The new (replacement) claim row id created by the adjustment.</summary>
    public required string SupersessorClaimId { get; init; }

    /// <summary>Operator-supplied reason for the adjustment (audit context).</summary>
    public required string AdjustmentReason { get; init; }

    /// <summary>Caller identity propagated through the audit chain.</summary>
    public string? ActorId { get; init; }

    public string? CorrelationId { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
