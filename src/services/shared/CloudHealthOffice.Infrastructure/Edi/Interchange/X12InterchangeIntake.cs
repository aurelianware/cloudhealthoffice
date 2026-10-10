using System.Diagnostics.Metrics;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

/// <summary>One file handed to <see cref="IX12InterchangeIntake.ReceiveAsync"/>.</summary>
public sealed class InterchangeIntakeRequest
{
    public required string TenantId { get; init; }
    public required string Content { get; init; }

    /// <summary>The transaction the intake path expects (837, 834, 270, ...), stored on the TA1 record.</summary>
    public string? TransactionType { get; init; }

    public string? FileName { get; init; }
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

    /// <summary>Envelope validation only: no duplicate check, nothing stored (validate-only endpoints).</summary>
    InterchangeIntakeResult Preview(string content);
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

    // No receipt is registered without a request, so Decide completes synchronously.
    public InterchangeIntakeResult Preview(string content) => Decide(content, null, CancellationToken.None).GetAwaiter().GetResult();

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
                    Sanitize(request.TransactionType), Sanitize(outcome.ControlNumber), Sanitize(outcome.Envelope.Header?.SenderId),
                    Sanitize(request.TenantId), outcome.Decision.NoteCode);
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
                _logger.LogError(ex, "Failed to store TA1 for interchange {ControlNumber}", Sanitize(outcome.ControlNumber));
            }
        }
        return result;
    }

    private async Task<InterchangeIntakeResult> Decide(string content, InterchangeIntakeRequest? receive, CancellationToken ct)
    {
        var read = X12EnvelopeReader.Read(content, _options.ToValidationOptions());
        if (!read.IsReadable)
            return new InterchangeIntakeResult { UnreadableReason = read.UnreadableReason ?? "The file is not an X12 interchange." };

        var now = _clock.GetUtcNow().UtcDateTime;
        var outcomes = new List<InterchangeOutcome>(read.Interchanges.Count);
        foreach (var env in read.Interchanges)
        {
            var decision = Ta1Decision.For(env.Findings, _options.NotedNoteCodes);
            var duplicate = false;

            // Only an envelope that is otherwise acceptable claims its control
            // number: a rejected one can be corrected and resent with the same ISA13.
            if (receive is not null && _options.DuplicateWindowDays > 0 && !decision.IsRejected && env.Header is not null)
            {
                var registered = await _store.TryRegisterReceiptAsync(
                    receive.TenantId, env.Header.SenderKey, env.Header.ControlNumber, now,
                    TimeSpan.FromDays(_options.DuplicateWindowDays), ct);
                if (!registered)
                {
                    duplicate = true;
                    env.Findings.Insert(0, new X12EnvelopeFinding(Ta1NoteCodes.DuplicateControlNumber,
                        $"ISA13 {env.Header.ControlNumber} was already received from this sender within {_options.DuplicateWindowDays} days."));
                    decision = Ta1Decision.For(env.Findings, _options.NotedNoteCodes);
                }
            }

            string? ta1 = null;
            string? ta1Control = null;
            var due = env.Header is not null && (env.Header.AckRequested == "1" || decision.AckCode != Ta1AckCodes.Accepted);
            if (due)
            {
                var control = Random.Shared.NextInt64(1, 1_000_000_000);
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

    internal static string Sanitize(string? value) =>
        value is null ? string.Empty : new string(value.Where(c => !char.IsControl(c)).Take(64).ToArray());
}

/// <summary>TA1 counters, on the shared CHO meter.</summary>
public static class X12InterchangeMetrics
{
    private static readonly Meter Meter = new(ChoMetrics.MeterName);

    /// <summary>TA1s generated and received. Dimensions: cho.ta1.direction, cho.ta1.ack_code, cho.ta1.note_code.</summary>
    public static readonly Counter<long> Ta1Acknowledgments =
        Meter.CreateCounter<long>("cho.edi.ta1.total", unit: "{acknowledgment}", description: "TA1 interchange acknowledgments generated and received");
}
