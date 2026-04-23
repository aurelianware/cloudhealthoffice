using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Records X12 275 envelopes the consumer could not route. The default
/// implementation
/// (<see cref="LoggingAttachment275DeadLetterSink"/>) emits a structured
/// warning with non-PHI fields only. A future production deployment can
/// substitute a Kafka-DLQ-producing implementation without changing the
/// consumer's code path — see PR 4 plan, deferred item #8.
/// </summary>
public interface IAttachment275DeadLetterSink
{
    /// <summary>
    /// Record an envelope that could not be processed.
    /// </summary>
    /// <param name="envelope">The deserialized envelope. Implementations
    /// MUST NOT log <see cref="Attachment275EnvelopeDto.RawX12"/>,
    /// <see cref="Attachment275EnvelopeDto.PatientFirstName"/>,
    /// <see cref="Attachment275EnvelopeDto.PatientLastName"/>, or
    /// <see cref="Attachment275EnvelopeDto.Notes"/> — those fields are
    /// PHI-adjacent.</param>
    /// <param name="reason">Short, controlled vocabulary string describing
    /// why routing failed. Examples: <c>"missing-tenantId"</c>,
    /// <c>"no-open-appeal-for-claim"</c>, <c>"handler-exception"</c>.</param>
    Task DeadLetterAsync(
        Attachment275EnvelopeDto envelope,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Record a raw message that failed to deserialize as an envelope.
    /// Implementations MUST NOT log <paramref name="rawMessage"/> — it
    /// may contain PHI inside the malformed body.
    /// </summary>
    Task DeadLetterMalformedAsync(
        string rawMessage,
        string reason,
        CancellationToken ct = default);
}
