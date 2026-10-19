using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>How a partner's TA1 lined up with what CHO sent.</summary>
public static class Ta1MatchStatus
{
    /// <summary>Matched to one outbound interchange, now updated.</summary>
    public const string Matched = "Matched";

    /// <summary>No interchange CHO recorded has that ISA13 for that partner. Stored, nothing updated.</summary>
    public const string Unmatched = "Unmatched";
}

public sealed class Ta1MatchResult
{
    public required ParsedTa1 Ta1 { get; init; }
    public required string Status { get; init; }
    public required string AcknowledgmentId { get; init; }
    public string? OutboundInterchangeId { get; init; }
    public string? TransactionType { get; init; }
    public string? SourceReference { get; init; }
    public string AckStatus => OutboundAckStatus.FromAckCode(Ta1.AckCode);
}

public sealed class Ta1ProcessingResult
{
    public List<Ta1MatchResult> Results { get; } = [];
    public int RejectedCount => Results.Count(r => r.Ta1.AckCode == Ta1AckCodes.Rejected);
}

/// <summary>
/// The outbound half of interchange control: remembers the envelope of every
/// interchange CHO sends, and applies the partner's TA1 to it.
/// </summary>
public interface IOutboundInterchangeTracker
{
    /// <summary>
    /// Records an interchange CHO is sending. Reads only the ISA (and ST01
    /// when <paramref name="transactionType"/> is null); keeps no content.
    /// Never throws: a failure is logged and null returned, so sending is
    /// never blocked by tracking.
    /// </summary>
    Task<OutboundInterchangeRecord?> RecordSentAsync(string tenantId, string ediContent, string? transactionType, string? sourceReference, CancellationToken ct = default);

    /// <summary>
    /// For an interchange that can be produced again on demand (a 277CA
    /// download): the first time, records it as <see cref="RecordSentAsync"/>
    /// does; after that, returns <paramref name="ediContent"/> with the ISA13
    /// (and IEA02) already recorded for this <paramref name="sourceReference"/>,
    /// so repeat downloads are one interchange and one outbound record.
    /// Never throws.
    /// </summary>
    Task<string> GetOrRecordSentAsync(string tenantId, string ediContent, string transactionType, string sourceReference, CancellationToken ct = default);

    /// <summary>
    /// Applies a partner's TA1 file: stores each TA1 and marks the matching
    /// outbound interchange Accepted, AcceptedWithErrors or Rejected.
    /// </summary>
    /// <exception cref="FormatException">The content is not a TA1 interchange.</exception>
    Task<Ta1ProcessingResult> ProcessInboundTa1Async(string tenantId, string content, string? fileName, CancellationToken ct = default);
}

public sealed class OutboundInterchangeTracker : IOutboundInterchangeTracker
{
    private readonly IX12InterchangeStore _store;
    private readonly ILogger<OutboundInterchangeTracker> _logger;
    private readonly TimeProvider _clock;

    public OutboundInterchangeTracker(IX12InterchangeStore store, ILogger<OutboundInterchangeTracker> logger, TimeProvider? clock = null)
    {
        _store = store;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetOrRecordSentAsync(string tenantId, string ediContent, string transactionType, string sourceReference, CancellationToken ct = default)
    {
        try
        {
            var existing = await _store.FindOutboundBySourceAsync(tenantId, transactionType, sourceReference, ct);
            if (existing is not null)
                return X12InterchangeRewriter.WithControlNumber(ediContent, existing.ControlNumber) ?? ediContent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Outbound {TransactionType} lookup failed; recording it as new", LogSanitizer.Sanitize(transactionType, 64));
        }
        await RecordSentAsync(tenantId, ediContent, transactionType, sourceReference, ct);
        return ediContent;
    }

    public async Task<OutboundInterchangeRecord?> RecordSentAsync(string tenantId, string ediContent, string? transactionType, string? sourceReference, CancellationToken ct = default)
    {
        try
        {
            var read = X12EnvelopeReader.Read(ediContent);
            var env = read.Interchanges.FirstOrDefault();
            if (!read.IsReadable || env?.Header is null)
            {
                _logger.LogWarning("Outbound {TransactionType} interchange not tracked: no readable ISA", LogSanitizer.Sanitize(transactionType, 64));
                return null;
            }

            var h = env.Header;
            var record = new OutboundInterchangeRecord
            {
                TenantId = tenantId,
                TransactionType = transactionType ?? env.TransactionSetIds.FirstOrDefault() ?? "unknown",
                ControlNumber = h.ControlNumber,
                SenderQualifier = h.SenderQualifier,
                SenderId = h.SenderId,
                ReceiverQualifier = h.ReceiverQualifier,
                ReceiverId = h.ReceiverId,
                AckRequested = h.AckRequested == "1",
                SourceReference = sourceReference,
                SentAt = _clock.GetUtcNow().UtcDateTime,
            };
            await _store.SaveOutboundAsync(record, ct);
            return record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Outbound {TransactionType} interchange not tracked", LogSanitizer.Sanitize(transactionType, 64));
            return null;
        }
    }

    public async Task<Ta1ProcessingResult> ProcessInboundTa1Async(string tenantId, string content, string? fileName, CancellationToken ct = default)
    {
        var parsed = Ta1Parser.Parse(content);
        var now = _clock.GetUtcNow().UtcDateTime;
        var result = new Ta1ProcessingResult();

        foreach (var interchange in parsed)
        {
            var partner = interchange.Header.SenderId;
            foreach (var ta1 in interchange.Acknowledgments)
            {
                // The TA1 comes from the partner CHO sent to: match on ISA13 and that partner,
                // never on ISA13 alone (control numbers are only unique per sender/receiver pair).
                var candidates = await _store.FindOutboundAsync(tenantId, ta1.AcknowledgedControlNumber, ct);
                var match = candidates
                    .Where(c => string.Equals(c.ReceiverId, partner, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.AckStatus == OutboundAckStatus.Pending ? 0 : 1)
                    .ThenByDescending(c => c.SentAt)
                    .FirstOrDefault();

                var ack = new InterchangeAcknowledgmentRecord
                {
                    TenantId = tenantId,
                    Direction = Ta1Direction.Received,
                    TransactionType = match?.TransactionType,
                    FileName = fileName,
                    AcknowledgedControlNumber = ta1.AcknowledgedControlNumber,
                    // The acknowledged interchange went from CHO (TA1 receiver) to the partner (TA1 sender).
                    SenderQualifier = interchange.Header.ReceiverQualifier,
                    SenderId = interchange.Header.ReceiverId,
                    ReceiverQualifier = interchange.Header.SenderQualifier,
                    ReceiverId = partner,
                    AckCode = ta1.AckCode,
                    NoteCode = ta1.NoteCode,
                    NoteDescription = ta1.NoteDescription,
                    Ta1ControlNumber = interchange.Header.ControlNumber,
                    Ta1Content = interchange.RawText,
                    OutboundInterchangeId = match?.Id,
                    CreatedAt = now,
                };
                await _store.SaveAcknowledgmentAsync(ack, ct);

                if (match is not null)
                {
                    match.AckStatus = OutboundAckStatus.FromAckCode(ta1.AckCode);
                    match.AckNoteCode = ta1.NoteCode;
                    match.AckNoteDescription = ta1.NoteDescription;
                    match.AckReceivedAt = now;
                    match.AcknowledgmentId = ack.Id;
                    await _store.SaveOutboundAsync(match, ct);
                }

                X12InterchangeMetrics.Ta1Acknowledgments.Add(1,
                    new KeyValuePair<string, object?>("cho.ta1.direction", Ta1Direction.Received),
                    new KeyValuePair<string, object?>("cho.ta1.ack_code", ta1.AckCode),
                    new KeyValuePair<string, object?>("cho.ta1.note_code", ta1.NoteCode));

                if (ta1.AckCode == Ta1AckCodes.Rejected)
                {
                    _logger.LogWarning(
                        "Partner {PartnerId} rejected outbound {TransactionType} interchange {ControlNumber} (TA105 {NoteCode}) for tenant {TenantId}; matched={Matched}",
                        LogSanitizer.Sanitize(partner, 64), LogSanitizer.Sanitize(match?.TransactionType ?? "unknown", 64),
                        LogSanitizer.Sanitize(ta1.AcknowledgedControlNumber, 64), LogSanitizer.Sanitize(ta1.NoteCode, 64),
                        LogSanitizer.Sanitize(tenantId, 64), match is not null);
                }
                else if (match is null)
                {
                    _logger.LogInformation(
                        "TA1 from {PartnerId} for interchange {ControlNumber} matches no tracked outbound interchange",
                        LogSanitizer.Sanitize(partner, 64), LogSanitizer.Sanitize(ta1.AcknowledgedControlNumber, 64));
                }

                result.Results.Add(new Ta1MatchResult
                {
                    Ta1 = ta1,
                    Status = match is null ? Ta1MatchStatus.Unmatched : Ta1MatchStatus.Matched,
                    AcknowledgmentId = ack.Id,
                    OutboundInterchangeId = match?.Id,
                    TransactionType = match?.TransactionType,
                    SourceReference = match?.SourceReference,
                });
            }
        }

        return result;
    }
}

/// <summary>Rewrites an interchange's control number.</summary>
public static class X12InterchangeRewriter
{
    /// <summary>
    /// <paramref name="edi"/> (one interchange) with ISA13 and IEA02 set to
    /// <paramref name="controlNumber"/>, or null when it has no readable envelope.
    /// </summary>
    public static string? WithControlNumber(string edi, string controlNumber)
    {
        var read = X12EnvelopeReader.Read(edi);
        var h = read.Interchanges.FirstOrDefault()?.Header;
        if (!read.IsReadable || h is null || read.Interchanges.Count != 1 || controlNumber.Length != 9) return null;

        var segments = edi.Split(h.SegmentTerminator);
        for (var i = 0; i < segments.Length; i++)
        {
            var trimmed = segments[i].TrimStart();
            var lead = segments[i][..(segments[i].Length - trimmed.Length)];
            if (trimmed.StartsWith("ISA" + h.ElementSeparator, StringComparison.Ordinal))
            {
                var e = trimmed.Split(h.ElementSeparator);
                if (e.Length > 13) e[13] = controlNumber;
                segments[i] = lead + string.Join(h.ElementSeparator, e);
            }
            else if (trimmed.StartsWith("IEA" + h.ElementSeparator, StringComparison.Ordinal))
            {
                var e = trimmed.Split(h.ElementSeparator);
                if (e.Length > 2) e[2] = controlNumber;
                segments[i] = lead + string.Join(h.ElementSeparator, e);
            }
        }
        return string.Join(h.SegmentTerminator, segments);
    }
}
