using ClaimsExaminerService.Models;

namespace ClaimsExaminerService.Services.Events;

/// <summary>
/// Publishes <c>ClaimAiExaminationCompletedEvent</c> to the
/// <c>ai-examination-events</c> Service Bus topic after a successful
/// write-back to claims-service. Capability 5.9.
///
/// <para>
/// First subscriber is 5.10 remittance generation; future capabilities
/// (denial-letter automation, work-queue prioritization) attach
/// additional subscriptions filtered by the <c>MessageType</c>
/// application property.
/// </para>
///
/// <para>
/// Implementations swallow transport failures and log them — the claim
/// DB already has the AI examination written via the HTTP endpoint, so
/// missing the event is a degraded-notification condition, not a data
/// integrity issue. Same posture as
/// <c>IClaimEventPublisher.PublishClaimPendedAsync</c>.
/// </para>
/// </summary>
public interface IAiExaminationEventPublisher
{
    Task PublishCompletedAsync(
        string claimId,
        string tenantId,
        AiExaminationDto examination,
        string? correlationId,
        CancellationToken ct);
}
