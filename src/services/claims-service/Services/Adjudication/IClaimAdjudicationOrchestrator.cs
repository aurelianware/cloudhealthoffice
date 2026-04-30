using ClaimsService.Models.Messaging;
using CloudHealthOffice.Infrastructure.Messaging;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// Runs the adjudication pipeline end-to-end for a single claim version
/// (capability 5.5). Triggered by a <see cref="ClaimVersionSubmittedMessage"/>
/// arriving on the <c>claim-version-events</c> Service Bus topic; emits a
/// <see cref="ClaimVersionAdjudicatedMessage"/> back onto the same topic
/// after the run completes.
///
/// <para>
/// Decisions worth surfacing here:
/// </para>
/// <list type="number">
///   <item><description>The orchestrator is the only consumer of the adjudication subscription. Future capabilities (5.10 / 5.11 / 5.12) add their own subscriptions to the same topic with their own filter rules; they do not piggy-back on this one.</description></item>
///   <item><description>Stage ordering is fixed in code, not config (Decision 6). Per-tenant configuration controls only enablement.</description></item>
///   <item><description>Short-circuit on terminal stage failure routes straight to <see cref="Stages.PersistenceStage"/> (Decision 7).</description></item>
///   <item><description>Idempotency: re-processing an already-adjudicated claim (the <see cref="Models.AdapterClaim.AdjudicationResult"/> is already populated) skips gracefully — logs and completes the message (Decision 12).</description></item>
/// </list>
/// </summary>
public interface IClaimAdjudicationOrchestrator
{
    Task AdjudicateAsync(
        ClaimVersionSubmittedMessage message,
        MessageContext messageContext,
        CancellationToken ct);
}
