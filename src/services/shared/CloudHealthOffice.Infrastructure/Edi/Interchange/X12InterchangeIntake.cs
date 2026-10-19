using System.Diagnostics.Metrics;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>Configuration section <c>X12Interchange</c>.</summary>
public sealed class X12InterchangeOptions
{
    public const string SectionName = "X12Interchange";

    /// <summary>Envelope validation and TA1 on intake. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long an ISA13 stays used per sender. A repeat inside the window is
    /// rejected with TA105 025. 0 or less turns the duplicate check off
    /// (e.g. a sandbox where the same sample file is uploaded repeatedly).
    /// </summary>
    public int DuplicateWindowDays { get; set; } = 365;

    /// <summary>
    /// Per intake path, whether the duplicate check runs (keyed by
    /// <see cref="InterchangeIntakeRequest.DuplicateCheckKey"/>: the
    /// transaction type, e.g. <c>837</c>, or <c>{type}-realtime</c> for a
    /// real-time call). Paths not listed are checked. Real-time 270s are off by
    /// default (<c>270-realtime</c>): a timeout retry, or a submitter that
    /// reuses one ISA13 on every real-time call, must still get its 271. When
    /// off, no receipt is written.
    /// </summary>
    public Dictionary<string, bool> DuplicateCheck { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["270-realtime"] = false };

    /// <summary>
    /// What to do with a test interchange (ISA15 = T). Reject answers TA1 R
    /// with TA105 020, so test data is never imported as production; Accept
    /// is for test environments. Unset: Reject in Production, Accept elsewhere.
    /// </summary>
    public TestInterchangeHandling? TestInterchangeHandling { get; set; }

    /// <summary>
    /// TA105 codes that are noted rather than rejecting: an interchange whose
    /// only findings are these is accepted with errors (TA104 E) and its
    /// contents processed. Empty by default: every envelope error rejects.
    /// </summary>
    public List<string> NotedNoteCodes { get; set; } = [];

    /// <summary>ISA08 values accepted. Empty (default) accepts any; otherwise others get TA105 009.</summary>
    public List<string> KnownReceiverIds { get; set; } = [];

    public List<string> SupportedVersions { get; set; } = ["00401", "00501"];

    /// <summary>TA1 sender when the received ISA07/ISA08 are unusable.</summary>
    public string FallbackSenderQualifier { get; set; } = "ZZ";

    public string FallbackSenderId { get; set; } = "CHO";

    internal X12EnvelopeValidationOptions ToValidationOptions() => new()
    {
        KnownReceiverIds = KnownReceiverIds,
        SupportedVersions = SupportedVersions,
    };
}

public enum TestInterchangeHandling
{
    /// <summary>ISA15 = T is rejected (TA1 R, TA105 020).</summary>
    Reject,

    /// <summary>ISA15 = T is processed like P (test environments).</summary>
    Accept,
}

/// <summary>One file handed to <see cref="IX12InterchangeIntake.ReceiveAsync"/>.</summary>
public sealed class InterchangeIntakeRequest
{
    public required string TenantId { get; init; }
    public required string Content { get; init; }

    /// <summary>The transaction the intake path expects (837, 834, 270, ...), stored on the TA1 record.</summary>
    public string? TransactionType { get; init; }

    public string? FileName { get; init; }

    /// <summary>A real-time (request/response) call rather than a batch file.</summary>
    public bool RealTime { get; init; }

    /// <summary>The <see cref="X12InterchangeOptions.DuplicateCheck"/> key for this path.</summary>
    public string DuplicateCheckKey => RealTime ? $"{TransactionType}-realtime" : TransactionType ?? string.Empty;
}

/// <summary>The TA1 decision for one received interchange.</summary>
public sealed class InterchangeOutcome
{
    public required X12InterchangeEnvelope Envelope { get; init; }
    public required Ta1Decision Decision { get; init; }
    public bool IsDuplicate { get; init; }

    /// <summary>The TA1 interchange, when one is due (ISA14 = 1, or the envelope has findings).</summary>
    public string? Ta1 { get; init; }

    public string? Ta1ControlNumber { get; init; }

    /// <summary>The stored TA1's id (<see cref="InterchangeAcknowledgmentRecord.Id"/>).</summary>
    public string? AcknowledgmentId { get; set; }

    /// <summary>When this request claimed the interchange's control number (null: not claimed).</summary>
    public DateTime? ReceiptClaimedAt { get; init; }

    public bool IsRejected => Decision.IsRejected;
    public string? ControlNumber => Envelope.ControlNumber;
    public string? NoteDescription => Ta1NoteCodes.Describe(Decision.NoteCode);
}

/// <summary>What intake decided for a file.</summary>
public sealed class InterchangeIntakeResult
{
    /// <summary>Set when the file has no addressable ISA, so no TA1 can be returned.</summary>
    public string? UnreadableReason { get; init; }

    public IReadOnlyList<InterchangeOutcome> Interchanges { get; init; } = [];

    /// <summary>
    /// The interchanges whose envelope was accepted (A or E), in file order:
    /// what the transaction parser (and its 999) should see. Null when none were.
    /// </summary>
    public string? AcceptedContent { get; init; }

    public bool IsRejected => UnreadableReason is not null || AcceptedContent is null;

    /// <summary>Every TA1 produced for the file, one interchange per received interchange, or null.</summary>
    public string? Ta1 => Interchanges.Any(i => i.Ta1 is not null)
        ? string.Join("\n", Interchanges.Where(i => i.Ta1 is not null).Select(i => i.Ta1))
        : null;

    public IReadOnlyList<string> AcknowledgmentIds =>
        Interchanges.Where(i => i.AcknowledgmentId is not null).Select(i => i.AcknowledgmentId!).ToList();

    /// <summary>A one-line reason per rejected interchange, for an API error body (no transaction content).</summary>
    public IReadOnlyList<string> RejectionReasons =>
        UnreadableReason is not null
            ? [UnreadableReason]
            : Interchanges.Where(i => i.IsRejected)
                .Select(i => $"Interchange {i.ControlNumber ?? "(unreadable ISA)"} rejected: TA105 {i.Decision.NoteCode} {i.NoteDescription} {i.Envelope.Findings.FirstOrDefault(f => f.NoteCode == i.Decision.NoteCode)?.Message}".TrimEnd())
                .ToList();
}

/// <summary>
/// The envelope gate every inbound X12 path runs before its transaction
/// parser: validates ISA/IEA, rejects duplicates, and produces (and stores)
/// the TA1. A rejected interchange never reaches the parser, so no 999 is
/// built for it.
/// </summary>
public interface IX12InterchangeIntake
{
    /// <summary>Validates, checks for duplicates, records the receipt and stores any TA1.</summary>
    Task<InterchangeIntakeResult> ReceiveAsync(InterchangeIntakeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Envelope validation only: no duplicate check, nothing stored, and no
    /// control number taken from the TA1 sequence (a preview TA1 carries
    /// ISA13 000000000).
    /// </summary>
    InterchangeIntakeResult Preview(string content);

    /// <summary>
    /// Gives back the control numbers <paramref name="result"/> claimed.
    /// Call it when processing failed after the envelope was accepted (a parse
    /// error, a 500), so the partner's resend is processed instead of being
    /// refused as a duplicate. Never throws.
    /// </summary>
    Task ReleaseAsync(InterchangeIntakeRequest request, InterchangeIntakeResult result, CancellationToken ct = default);
}

public sealed class X12InterchangeIntake : IX12InterchangeIntake
{
    private static readonly Counter<long> Ta1Counter = X12InterchangeMetrics.Ta1Acknowledgments;

    private readonly IX12InterchangeStore _store;
    private readonly X12InterchangeOptions _options;
    private readonly ILogger<X12InterchangeIntake> _logger;
    private readonly TimeProvider _clock;

    public X12InterchangeIntake(
        IX12InterchangeStore store,
        IOptions<X12InterchangeOptions> options,
        ILogger<X12InterchangeIntake> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public InterchangeIntakeResult Preview(string content)
    {
        // Preview is synchronous by contract (IX12InterchangeIntake). Without a receive
        // request Decide never reaches the store (no duplicate check, no TA1 control
        // number), so the task is already complete here and GetResult does not block.
        // Refuse rather than block should that ever stop being true.
        var decided = Decide(content, null, CancellationToken.None);
        if (!decided.IsCompleted)
            throw new InvalidOperationException("Interchange preview must not perform I/O.");
        return decided.GetAwaiter().GetResult();
    }

    public async Task<InterchangeIntakeResult> ReceiveAsync(InterchangeIntakeRequest request, CancellationToken ct = default)
    {
        var result = await Decide(request.Content, request, ct);
        foreach (var outcome in result.Interchanges)
        {
            Ta1Counter.Add(1,
                new KeyValuePair<string, object?>("cho.ta1.direction", Ta1Direction.Generated),
                new KeyValuePair<string, object?>("cho.ta1.ack_code", outcome.Decision.AckCode),
                new KeyValuePair<string, object?>("cho.ta1.note_code", outcome.Decision.NoteCode));

            if (outcome.IsRejected)
            {
                _logger.LogWarning(
                    "Rejected {TransactionType} interchange {ControlNumber} from sender {SenderId} for tenant {TenantId}: TA105 {NoteCode}",
                    LogSanitizer.Sanitize(request.TransactionType, 64), LogSanitizer.Sanitize(outcome.ControlNumber, 64), LogSanitizer.Sanitize(outcome.Envelope.Header?.SenderId, 64),
                    LogSanitizer.Sanitize(request.TenantId, 64), outcome.Decision.NoteCode);
            }

            if (outcome.Ta1 is null) continue;
            var record = ToRecord(request, outcome);
            try
            {
                await _store.SaveAcknowledgmentAsync(record, ct);
                outcome.AcknowledgmentId = record.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The TA1 still goes back in the response; only its stored copy is missing.
                _logger.LogError(ex, "Failed to store TA1 for interchange {ControlNumber}", LogSanitizer.Sanitize(outcome.ControlNumber, 64));
            }
        }
        return result;
    }

    private async Task<InterchangeIntakeResult> Decide(string content, InterchangeIntakeRequest? receive, CancellationToken ct)
    {
        var read = X12EnvelopeReader.Read(content, _options.ToValidationOptions());
        if (!read.IsReadable)
            return new InterchangeIntakeResult { UnreadableReason = read.UnreadableReason ?? "The file is not an X12 interchange." };

        // Millisecond precision, as stored, so a release can match its own receipt.
        var clock = _clock.GetUtcNow().UtcDateTime;
        var now = new DateTime(clock.Ticks - clock.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var outcomes = new List<InterchangeOutcome>(read.Interchanges.Count);
        foreach (var env in read.Interchanges)
        {
            if (env.Header?.UsageIndicator == "T" && (_options.TestInterchangeHandling ?? TestInterchangeHandling.Accept) == TestInterchangeHandling.Reject
                && env.Findings.All(f => f.NoteCode != Ta1NoteCodes.InvalidTestIndicator))
                env.Findings.Add(new X12EnvelopeFinding(Ta1NoteCodes.InvalidTestIndicator,
                    "ISA15 is T (test data); this environment accepts production interchanges only."));

            var decision = Ta1Decision.For(env.Findings, _options.NotedNoteCodes);
            var duplicate = false;
            DateTime? claimedAt = null;

            // Only an envelope that is otherwise acceptable claims its control
            // number: a rejected one can be corrected and resent with the same ISA13.
            if (receive is not null && DuplicateCheckApplies(receive) && !decision.IsRejected && env.Header is not null)
            {
                var claimed = await _store.TryRegisterReceiptAsync(
                    receive.TenantId, env.Header.PartyKey, env.Header.ControlNumber, now,
                    TimeSpan.FromDays(_options.DuplicateWindowDays), ct);
                if (claimed)
                {
                    claimedAt = now;
                }
                else
                {
                    duplicate = true;
                    env.Findings.Insert(0, new X12EnvelopeFinding(Ta1NoteCodes.DuplicateControlNumber,
                        $"ISA13 {env.Header.ControlNumber} was already received from this sender for this receiver within {_options.DuplicateWindowDays} days."));
                    decision = Ta1Decision.For(env.Findings, _options.NotedNoteCodes);
                }
            }

            string? ta1 = null;
            string? ta1Control = null;
            var due = env.Header is not null && (env.Header.AckRequested == "1" || decision.AckCode != Ta1AckCodes.Accepted);
            if (due)
            {
                // The TA1's own ISA13: the next number of the sequence for this
                // sender/receiver pair (our ISA06 to the partner), never random.
                var control = receive is null
                    ? 0
                    : await _store.NextControlNumberAsync(receive.TenantId, $"{env.Header!.ReceiverKey}>{env.Header.SenderKey}", ct);
                ta1 = Ta1Builder.Build(env, decision, new Ta1Builder.Options
                {
                    ControlNumber = control,
                    Now = now,
                    FallbackSenderQualifier = _options.FallbackSenderQualifier,
                    FallbackSenderId = _options.FallbackSenderId,
                });
                ta1Control = control.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
            }

            outcomes.Add(new InterchangeOutcome
            {
                Envelope = env,
                Decision = decision,
                IsDuplicate = duplicate,
                ReceiptClaimedAt = claimedAt,
                Ta1 = ta1,
                Ta1ControlNumber = ta1Control,
            });
        }

        var accepted = outcomes.Where(o => !o.IsRejected).Select(o => o.Envelope.RawText).ToList();
        return new InterchangeIntakeResult
        {
            Interchanges = outcomes,
            AcceptedContent = accepted.Count == 0 ? null : string.Join("\n", accepted),
        };
    }

    private bool DuplicateCheckApplies(InterchangeIntakeRequest request) =>
        _options.DuplicateWindowDays > 0
        && (!_options.DuplicateCheck.TryGetValue(request.DuplicateCheckKey, out var on) || on);

    public async Task ReleaseAsync(InterchangeIntakeRequest request, InterchangeIntakeResult result, CancellationToken ct = default)
    {
        foreach (var outcome in result.Interchanges.Where(o => o.ReceiptClaimedAt is not null && o.Envelope.Header is not null))
        {
            try
            {
                await _store.ReleaseReceiptAsync(request.TenantId, outcome.Envelope.Header!.PartyKey, outcome.Envelope.Header.ControlNumber,
                    outcome.ReceiptClaimedAt!.Value, ct);
                _logger.LogInformation("Released ISA13 {ControlNumber} of a {TransactionType} interchange whose processing failed; a resend is accepted",
                    LogSanitizer.Sanitize(outcome.ControlNumber, 64), LogSanitizer.Sanitize(request.TransactionType, 64));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not release ISA13 {ControlNumber}; a resend will be refused as a duplicate", LogSanitizer.Sanitize(outcome.ControlNumber, 64));
            }
        }
    }

    private static InterchangeAcknowledgmentRecord ToRecord(InterchangeIntakeRequest request, InterchangeOutcome o)
    {
        var h = o.Envelope.Header!;
        return new InterchangeAcknowledgmentRecord
        {
            TenantId = request.TenantId,
            Direction = Ta1Direction.Generated,
            TransactionType = request.TransactionType,
            FileName = request.FileName,
            AcknowledgedControlNumber = Ta1Builder.AckedControlNumber(h.ControlNumber),
            SenderQualifier = h.SenderQualifier,
            SenderId = h.SenderId,
            ReceiverQualifier = h.ReceiverQualifier,
            ReceiverId = h.ReceiverId,
            AckCode = o.Decision.AckCode,
            NoteCode = o.Decision.NoteCode,
            NoteDescription = o.NoteDescription,
            Findings = o.Envelope.Findings.Select(f => new InterchangeFindingRecord { NoteCode = f.NoteCode, Message = f.Message }).ToList(),
            Ta1ControlNumber = o.Ta1ControlNumber,
            Ta1Content = o.Ta1,
        };
    }
}

/// <summary>TA1 counters, on the shared CHO meter.</summary>
public static class X12InterchangeMetrics
{
    private static readonly Meter Meter = new(ChoMetrics.MeterName);

    /// <summary>TA1s generated and received. Dimensions: cho.ta1.direction, cho.ta1.ack_code, cho.ta1.note_code.</summary>
    public static readonly Counter<long> Ta1Acknowledgments =
        Meter.CreateCounter<long>("cho.edi.ta1.total", unit: "{acknowledgment}", description: "TA1 interchange acknowledgments generated and received");
}
