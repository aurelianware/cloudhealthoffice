using System.Collections.Concurrent;
using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// Captures every dead-letter call so consumer tests can assert
/// the routing decision tree. Mirrors the queue-per-call shape that
/// <see cref="RecordingAppealEventPublisher"/> uses.
/// </summary>
public sealed class RecordingAttachment275DeadLetterSink : IAttachment275DeadLetterSink
{
    public readonly ConcurrentQueue<EnvelopeCall> Envelopes = new();
    public readonly ConcurrentQueue<MalformedCall> Malformed = new();

    public Task DeadLetterAsync(Attachment275EnvelopeDto envelope, string reason, CancellationToken ct = default)
    {
        Envelopes.Enqueue(new EnvelopeCall(envelope.TenantId, envelope.ClaimId, envelope.ControlNumber, reason));
        return Task.CompletedTask;
    }

    public Task DeadLetterMalformedAsync(string rawMessage, string reason, CancellationToken ct = default)
    {
        Malformed.Enqueue(new MalformedCall(rawMessage?.Length ?? 0, reason));
        return Task.CompletedTask;
    }

    public sealed record EnvelopeCall(string TenantId, string? ClaimId, string? ControlNumber, string Reason);
    public sealed record MalformedCall(int RawCharLength, string Reason);
}
