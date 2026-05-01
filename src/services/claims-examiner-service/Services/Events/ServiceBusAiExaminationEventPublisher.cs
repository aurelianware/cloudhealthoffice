using ClaimsExaminerService.Models;
using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Messaging;

namespace ClaimsExaminerService.Services.Events;

/// <summary>
/// Service Bus implementation of <see cref="IAiExaminationEventPublisher"/>.
/// Wraps <see cref="IMessageBus.SendAsync{T}"/> to topic
/// <see cref="AiExaminationEventTopics.TopicName"/>. Capability 5.9.
///
/// <para>
/// <b>Idempotency (Plan-First Decision 15 / Gap E.1).</b>
/// <c>SendOptions.MessageId</c> = <c>"ai-completed:{claimId}"</c> — one
/// logical event per claim. AI examination is terminal for the pend
/// cycle (Decision 3); re-emissions within the Service Bus dedup
/// window (default 1 hour) are dropped at the broker. The disposition
/// is intentionally NOT part of the key — different invocations could
/// yield different recommendations (e.g., RFAI fetch retry succeeds
/// on second attempt), and using disposition would let two distinct
/// keys coexist for the same logical completion.
/// </para>
///
/// <para>
/// <b>Degraded posture.</b> Mirrors <c>ClaimEventPublisher</c>: failures
/// are logged and swallowed. The claim DB already has the AI examination
/// written via the HTTP endpoint, so missing the event is a degraded-
/// notification condition that ops triages from telemetry — never a
/// data integrity issue.
/// </para>
/// </summary>
public class ServiceBusAiExaminationEventPublisher : IAiExaminationEventPublisher
{
    private readonly IMessageBus _bus;
    private readonly ILogger<ServiceBusAiExaminationEventPublisher> _logger;

    public ServiceBusAiExaminationEventPublisher(
        IMessageBus bus,
        ILogger<ServiceBusAiExaminationEventPublisher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public async Task PublishCompletedAsync(
        string claimId,
        string tenantId,
        AiExaminationDto examination,
        string? correlationId,
        CancellationToken ct)
    {
        var payload = new ClaimAiExaminationCompletedEvent
        {
            ClaimId = claimId,
            TenantId = tenantId,
            RecommendedDisposition = examination.RecommendedDisposition,
            ConfidenceScore = examination.ConfidenceScore,
            CompletedAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        };

        var sendOptions = new SendOptions(
            MessageId: $"ai-completed:{claimId}",
            CorrelationId: correlationId,
            Properties: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiExaminationEventTopics.MessageTypeProperty] =
                    AiExaminationEventTopics.CompletedMessageType,
            });

        try
        {
            await _bus
                .SendAsync(AiExaminationEventTopics.TopicName, payload, sendOptions, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Published ClaimAiExaminationCompletedEvent for claim {ClaimId} (disposition={Disposition})",
                claimId, examination.RecommendedDisposition);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish ClaimAiExaminationCompletedEvent for claim {ClaimId}; claim DB already has the AI recommendation written, downstream notification degraded",
                claimId);
        }
    }
}
