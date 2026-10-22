using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Edi.Files;

/// <summary>One file as an intake path received it.</summary>
public sealed class EdiFileIntake
{
    public required string TenantId { get; init; }
    public required string Content { get; init; }

    /// <summary>The transaction the intake path expects (837, 834, ...).</summary>
    public string? TransactionType { get; init; }

    /// <summary><see cref="EdiFileSource"/>; anything else is recorded as Upload.</summary>
    public string? Source { get; init; }

    public string? FileName { get; init; }
    public string? SourcePath { get; init; }
    public string? ReceivedBy { get; init; }
}

/// <summary>What processing the file's accepted contents produced.</summary>
public sealed class EdiFileCompletion
{
    public int AcceptedRecords { get; init; }
    public int RejectedRecords { get; init; }

    /// <summary>The 999 sent for the file: AK901 and its ISA13.</summary>
    public string? Ack999Code { get; init; }
    public string? Ack999ControlNumber { get; init; }

    /// <summary>File-level errors (unreadable contents, no transactions). Record errors stay on the records.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Writes the <see cref="EdiFile"/> record for every file an intake path
/// receives (docs/architecture/edi-operations.md §3–4). Recording is
/// best-effort: a store failure is logged and never fails the intake, the
/// same posture as the per-record transaction logs.
/// </summary>
/// <summary>An outbound file CHO generated, after its control numbers were assigned.</summary>
public sealed class EdiFileOutbound
{
    public required string TenantId { get; init; }
    public required string Content { get; init; }

    /// <summary>The label of what was sent (999, 277CA, 271, 835 ...).</summary>
    public string? TransactionType { get; init; }

    public string? SourceReference { get; init; }

    public IReadOnlyList<string> OutboundInterchangeIds { get; init; } = [];
}

public interface IEdiFileRecorder
{
    /// <summary>
    /// Records a file CHO sent (direction Outbound, source Generated, status
    /// Sent). Never throws; returns null when the record could not be written.
    /// </summary>
    Task<EdiFile?> RecordSentAsync(EdiFileOutbound outbound, CancellationToken ct = default);

    /// <summary>
    /// Records a received file after the envelope gate. <paramref name="envelope"/>
    /// is the intake's decision (null when the gate is off). Returns the record,
    /// or null when it could not be stored.
    /// </summary>
    Task<EdiFile?> RecordReceivedAsync(EdiFileIntake intake, InterchangeIntakeResult? envelope, CancellationToken ct = default);

    /// <summary>Records the outcome of processing the accepted contents. No-op when <paramref name="file"/> is null.</summary>
    Task CompleteAsync(EdiFile? file, EdiFileCompletion completion, CancellationToken ct = default);
}

public sealed class EdiFileRecorder : IEdiFileRecorder
{
    private readonly IEdiFileStore _store;
    private readonly ILogger<EdiFileRecorder> _logger;
    private readonly TimeProvider _clock;

    public EdiFileRecorder(IEdiFileStore store, ILogger<EdiFileRecorder> logger, TimeProvider? clock = null)
    {
        _store = store;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EdiFile?> RecordReceivedAsync(EdiFileIntake intake, InterchangeIntakeResult? envelope, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var file = Build(intake, envelope, now);
        try
        {
            await _store.CreateAsync(file, ct);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record received {TransactionType} file {FileName}",
                LogSanitizer.Sanitize(intake.TransactionType, 16), LogSanitizer.Sanitize(intake.FileName));
            return null;
        }
    }

    public async Task<EdiFile?> RecordSentAsync(EdiFileOutbound outbound, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        try
        {
            var file = Build(new EdiFileIntake
            {
                TenantId = outbound.TenantId,
                Content = outbound.Content,
                TransactionType = outbound.TransactionType,
                Source = EdiFileSource.Generated,
            }, envelope: null, now);
            file.Direction = EdiFileDirection.Outbound;
            file.SourceReference = outbound.SourceReference;
            file.OutboundInterchangeIds.AddRange(outbound.OutboundInterchangeIds);
            file.History.Clear();
            Move(file, EdiFileStatus.Sent, now, null);
            await _store.CreateAsync(file, ct);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record sent {TransactionType} file", LogSanitizer.Sanitize(outbound.TransactionType, 16));
            return null;
        }
    }

    public async Task CompleteAsync(EdiFile? file, EdiFileCompletion completion, CancellationToken ct = default)
    {
        if (file is null) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        file.AcceptedRecords = completion.AcceptedRecords;
        file.RejectedRecords = completion.RejectedRecords;
        file.Ack999Code = completion.Ack999Code ?? file.Ack999Code;
        file.Ack999ControlNumber = completion.Ack999ControlNumber ?? file.Ack999ControlNumber;
        file.Errors.AddRange(completion.Errors);

        var parsed = completion.AcceptedRecords + completion.RejectedRecords > 0;
        if (parsed)
            Move(file, EdiFileStatus.Parsed, now, $"{completion.AcceptedRecords + completion.RejectedRecords} record(s)");
        if (completion.AcceptedRecords > 0)
            Move(file, EdiFileStatus.Applied, now, $"{completion.AcceptedRecords} accepted, {completion.RejectedRecords} rejected");
        else
            Move(file, EdiFileStatus.Rejected, now,
                completion.Errors.FirstOrDefault() ?? (parsed ? "Every record was rejected" : "Nothing could be processed"));
        try
        {
            await _store.SaveAsync(file, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record the outcome of EDI file {FileId}", file.Id);
        }
    }

    internal static EdiFile Build(EdiFileIntake intake, InterchangeIntakeResult? envelope, DateTime now)
    {
        var bytes = Encoding.UTF8.GetBytes(intake.Content);
        var file = new EdiFile
        {
            TenantId = intake.TenantId,
            Direction = EdiFileDirection.Inbound,
            TransactionType = intake.TransactionType,
            Source = EdiFileSource.Normalize(intake.Source),
            FileName = intake.FileName,
            SourcePath = string.IsNullOrWhiteSpace(intake.SourcePath) ? null : intake.SourcePath.Trim(),
            ReceivedBy = intake.ReceivedBy,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ReceivedAt = now,
            UpdatedAt = now,
        };
        Move(file, EdiFileStatus.Received, now, null);

        // The envelopes: the intake's when the gate ran, otherwise read here
        // (identifiers only; nothing is validated or claimed).
        var interchanges = envelope?.Interchanges.Select(o => o.Envelope).ToList()
                           ?? X12EnvelopeReader.Read(intake.Content).Interchanges.ToList();
        var first = interchanges.FirstOrDefault(i => i.Header is not null)?.Header;
        if (first is not null)
        {
            file.SenderQualifier = first.SenderQualifier;
            file.SenderId = first.SenderId;
            file.ReceiverQualifier = first.ReceiverQualifier;
            file.ReceiverId = first.ReceiverId;
            file.Version = first.Version;
            file.UsageIndicator = first.UsageIndicator;
        }
        foreach (var interchange in interchanges)
        {
            if (interchange.ControlNumber is { Length: > 0 } isa13) AddOnce(file.InterchangeControlNumbers, isa13.Trim());
            foreach (var id in interchange.TransactionSetIds) AddOnce(file.TransactionSetIds, id);
            if (interchange.Header is { } h) ReadInnerControlNumbers(interchange.RawText, h, file);
        }

        if (envelope is null)
            return file;

        if (envelope.UnreadableReason is not null)
        {
            file.Errors.Add(envelope.UnreadableReason);
            Move(file, EdiFileStatus.Rejected, now, envelope.UnreadableReason);
            return file;
        }

        var ta1 = envelope.Interchanges.FirstOrDefault(i => i.Ta1 is not null);
        file.Ta1ControlNumber = ta1?.Ta1ControlNumber;
        file.Ta1AckCode = envelope.Interchanges.Any(i => i.IsRejected) ? Ta1AckCodes.Rejected
            : envelope.Interchanges.Any(i => i.Decision.AckCode == Ta1AckCodes.AcceptedWithErrors) ? Ta1AckCodes.AcceptedWithErrors
            : ta1 is null ? null : Ta1AckCodes.Accepted;
        file.Errors.AddRange(envelope.RejectionReasons);

        Move(file, EdiFileStatus.EnvelopeChecked, now, null);
        if (envelope.IsRejected)
        {
            var allDuplicates = envelope.Interchanges.Count > 0 && envelope.Interchanges.All(i => i.IsDuplicate);
            Move(file, allDuplicates ? EdiFileStatus.Duplicate : EdiFileStatus.Rejected, now, envelope.RejectionReasons.FirstOrDefault());
        }
        else if (ta1 is not null)
        {
            Move(file, EdiFileStatus.Acknowledged, now, $"TA1 {file.Ta1AckCode}");
        }
        return file;
    }

    private static void Move(EdiFile file, string status, DateTime at, string? detail)
    {
        file.Status = status;
        file.UpdatedAt = at;
        file.History.Add(new EdiFileStatusChange { Status = status, At = at, Detail = detail });
    }

    private static void AddOnce(List<string> list, string value)
    {
        if (value.Length > 0 && !list.Contains(value)) list.Add(value);
    }

    /// <summary>GS06 and ST02 of every group and transaction set in one interchange.</summary>
    private static void ReadInnerControlNumbers(string rawText, X12InterchangeHeader header, EdiFile file)
    {
        foreach (var raw in rawText.Split(header.SegmentTerminator))
        {
            var segment = raw.Trim('\r', '\n', ' ');
            if (segment.StartsWith("GS" + header.ElementSeparator, StringComparison.Ordinal))
            {
                var e = segment.Split(header.ElementSeparator);
                if (e.Length > 6) AddOnce(file.GroupControlNumbers, e[6].Trim());
            }
            else if (segment.StartsWith("ST" + header.ElementSeparator, StringComparison.Ordinal))
            {
                var e = segment.Split(header.ElementSeparator);
                if (e.Length > 2) AddOnce(file.TransactionSetControlNumbers, e[2].Trim());
            }
        }
    }
}
