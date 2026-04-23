using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Default <see cref="IAttachment275DeadLetterSink"/> implementation:
/// emits a structured warning per dead-lettered envelope. The fields it
/// logs (<c>tenantId</c>, <c>claimId</c>, <c>controlNumber</c>, <c>reason</c>)
/// are deliberately the non-PHI subset — see
/// <c>AppealEventPublisherTests</c>'s field-whitelist posture for the
/// equivalent invariant on the outbound event stream.
/// </summary>
public sealed class LoggingAttachment275DeadLetterSink : IAttachment275DeadLetterSink
{
    private readonly ILogger<LoggingAttachment275DeadLetterSink> _logger;

    public LoggingAttachment275DeadLetterSink(ILogger<LoggingAttachment275DeadLetterSink> logger)
    {
        _logger = logger;
    }

    public Task DeadLetterAsync(Attachment275EnvelopeDto envelope, string reason, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "275 dead-letter: tenantId={TenantId} claimId={ClaimId} controlNumber={ControlNumber} reason={Reason}",
            LogSanitizer.SafeForLog(envelope.TenantId),
            LogSanitizer.SafeForLog(envelope.ClaimId),
            LogSanitizer.SafeForLog(envelope.ControlNumber),
            LogSanitizer.SafeForLog(reason));
        return Task.CompletedTask;
    }

    public Task DeadLetterMalformedAsync(string rawMessage, string reason, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "275 dead-letter (malformed): bytes={Bytes} reason={Reason}",
            rawMessage?.Length ?? 0,
            LogSanitizer.SafeForLog(reason));
        return Task.CompletedTask;
    }
}
