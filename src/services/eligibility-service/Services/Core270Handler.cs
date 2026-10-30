using System.Globalization;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Core;
using CloudHealthOffice.Infrastructure.Edi.Interchange;

namespace EligibilityService.Services;

/// <summary>
/// Answers CAQH CORE real-time 270 envelopes with the same pipeline as
/// <c>POST api/eligibility/270</c>. Per the CORE real-time acknowledgment
/// rule the response payload is the 271 when the 270 was processed, a TA1
/// when its interchange envelope was rejected, or a 999 when its content was.
/// When coverage cannot be checked, the 271 says so with AAA 42 (unable to
/// respond at current time) rather than an envelope error.
/// </summary>
public sealed class Core270Handler : ICoreRealTimeHandler
{
    private readonly IRealTime270Service _realTime;
    private readonly IEdi270Parser _parser;
    private readonly IOutboundInterchangeTracker? _outboundTracker;
    private readonly TimeProvider _time;

    public Core270Handler(
        IRealTime270Service realTime,
        IEdi270Parser parser,
        IOutboundInterchangeTracker? outboundTracker = null,
        TimeProvider? time = null)
    {
        _realTime = realTime;
        _parser = parser;
        _outboundTracker = outboundTracker;
        _time = time ?? TimeProvider.System;
    }

    public string RequestPayloadType => CorePayloadTypes.Eligibility270;

    public async Task<CorePayloadResult> HandleAsync(CoreRealTimeRequest request, CancellationToken ct)
    {
        var outcome = await _realTime.RespondAsync(request.TenantId, request.Payload, ct);
        switch (outcome.Kind)
        {
            case RealTime270OutcomeKind.Answered:
                return new CorePayloadResult(CorePayloadTypes.Eligibility271, outcome.Edi!, CorePayloadResult.Outcomes.Answered);

            case RealTime270OutcomeKind.EnvelopeRejected when outcome.Edi is not null:
                return new CorePayloadResult(CorePayloadTypes.AcknowledgmentTa1, outcome.Edi, CorePayloadResult.Outcomes.RejectedTa1);

            case RealTime270OutcomeKind.EnvelopeRejected:
                throw new CorePayloadUnreadableException("The payload has no readable X12 interchange header.");

            case RealTime270OutcomeKind.Invalid:
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var edi999 = X12999Rejection.Build(outcome.AcceptedContent ?? request.Payload, TimeControlNumber(now), now)
                    ?? throw new CorePayloadUnreadableException("The payload has no readable functional group.");
                if (_outboundTracker is not null)
                    edi999 = await _outboundTracker.GetOrRecordSentAsync(request.TenantId, edi999, "999", $"core:{request.PayloadId}", ct);
                return new CorePayloadResult(CorePayloadTypes.Acknowledgment999, edi999, CorePayloadResult.Outcomes.Rejected999);
            }

            default:
                return UnableToRespond(request)
                    ?? throw new CorePayloadUnreadableException("The payload could not be read.");
        }
    }

    public CorePayloadResult? UnableToRespond(CoreRealTimeRequest request)
    {
        Edi270ParseResult parsed;
        try
        {
            parsed = _parser.Parse(request.Payload);
        }
        catch (Exception)
        {
            return null;
        }
        var now = _time.GetUtcNow().UtcDateTime;
        return new CorePayloadResult(
            CorePayloadTypes.Eligibility271,
            Edi271UnableToRespond.Build(parsed, TimeControlNumber(now), now),
            CorePayloadResult.Outcomes.UnableToRespond);
    }

    // Sequenced numbers replace this when the outbound tracker is on; without
    // it, the time of day keeps successive responses distinct.
    private static long TimeControlNumber(DateTime now) =>
        (long)now.TimeOfDay.TotalMilliseconds % 1_000_000_000 + 1;
}

/// <summary>
/// The 271 for a 270 CHO cannot answer right now: the information source
/// (2100A) carries AAA*Y**42*R (unable to respond at current time;
/// resubmission allowed), and no subscriber loops follow.
/// </summary>
public static class Edi271UnableToRespond
{
    public static string Build(Edi270ParseResult parsed, long controlNumber, DateTime now)
    {
        var inquiry = parsed.Inquiry;
        var control = (controlNumber % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
        var group = (controlNumber % 1_000_000_000).ToString(CultureInfo.InvariantCulture);
        var date6 = now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var date8 = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var time = now.ToString("HHmm", CultureInfo.InvariantCulture);

        var sender = Clean(parsed.InterchangeReceiverId, "CHO");
        var receiver = Clean(parsed.InterchangeSenderId, "SUBMITTER");
        var gsSender = Clean(parsed.ApplicationReceiverId, sender);
        var gsReceiver = Clean(parsed.ApplicationSenderId, receiver);
        var payerName = Clean(inquiry.PayerName, "HEALTH PLAN");
        var payerId = Clean(inquiry.PayerId, gsSender);
        var reference = Clean(inquiry.ControlNumber, control);

        var body = new List<string>
        {
            "ST*271*0001*005010X279A1",
            $"BHT*0022*11*{reference}*{date8}*{time}",
            "HL*1**20*0",
            $"NM1*PR*2*{payerName}*****PI*{payerId}",
            "AAA*Y**42*R",
        };
        body.Add($"SE*{body.Count + 1}*0001");

        var sb = new StringBuilder();
        sb.Append($"ISA*00*          *00*          *ZZ*{sender.PadRight(15)[..15]}*ZZ*{receiver.PadRight(15)[..15]}*{date6}*{time}*^*00501*{control}*0*P*:~");
        sb.Append($"GS*HB*{gsSender}*{gsReceiver}*{date8}*{time}*{group}*X*005010X279A1~");
        foreach (var segment in body)
            sb.Append(segment).Append('~');
        sb.Append($"GE*1*{group}~");
        sb.Append($"IEA*1*{control}~");
        return sb.ToString();
    }

    private static string Clean(string? value, string fallback)
    {
        var v = new string((value ?? string.Empty).Trim().Where(c => c is not ('*' or '~' or ':' or '^')).ToArray());
        return v.Length == 0 ? fallback : v;
    }
}
