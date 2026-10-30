using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Edi.Core;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.Extensions.Options;

namespace ClaimsService.EDI.ClaimStatusInquiry;

/// <summary>
/// Finds the claims a 276 inquiry is about. A claim is only ever returned
/// when it belongs to the subscriber the inquiry names (its member or
/// subscriber id), so knowing a claim number is not enough to read a claim.
/// </summary>
public sealed class ClaimStatusLookup
{
    public const int MaxClaimsPerInquiry = 10;

    private readonly IClaimRepository _claims;

    public ClaimStatusLookup(IClaimRepository claims) => _claims = claims;

    public async Task<IReadOnlyList<Claim>> FindAsync(ClaimStatusInquiryItem q)
    {
        if (string.IsNullOrWhiteSpace(q.SubscriberId))
            return [];

        IEnumerable<Claim> candidates;
        if (q.PayerClaimControlNumber is not null)
        {
            candidates = await _claims.GetByIdAsync(q.PayerClaimControlNumber) is { } byId ? [byId] : [];
        }
        else if (q.PatientControlNumber is not null)
        {
            candidates = await _claims.GetByClaimNumberAsync(q.PatientControlNumber) is { } byNumber ? [byNumber] : [];
        }
        else
        {
            candidates = await _claims.SearchAsync(
                memberId: q.SubscriberId,
                providerNPI: q.ProviderNpi,
                serviceDateFrom: q.ServiceDateFrom,
                serviceDateTo: q.ServiceDateTo,
                status: null,
                lineOfBusiness: null,
                page: 1,
                pageSize: MaxClaimsPerInquiry * 2);
        }

        return candidates
            .Where(c => string.Equals(c.MemberId, q.SubscriberId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.SubscriberId, q.SubscriberId, StringComparison.OrdinalIgnoreCase))
            .Where(c => q.ProviderNpi is null
                        || c.BillingProviderNPI == q.ProviderNpi
                        || c.RenderingProviderNPI == q.ProviderNpi)
            .Where(c => q.TotalChargeAmount is null || c.TotalChargeAmount == q.TotalChargeAmount)
            .Where(c => q.ServiceDateFrom is null
                        || (c.ServiceDateFrom.Date <= (q.ServiceDateTo ?? q.ServiceDateFrom).Value.Date
                            && (c.ServiceDateTo == default ? c.ServiceDateFrom : c.ServiceDateTo).Date >= q.ServiceDateFrom.Value.Date))
            .Where(c => !q.IsDependent || q.DependentLastName is null
                        || string.Equals(c.PatientLastName, q.DependentLastName, StringComparison.OrdinalIgnoreCase))
            .Take(MaxClaimsPerInquiry)
            .ToList();
    }
}

public enum RealTime276OutcomeKind
{
    Answered,
    EnvelopeRejected,
    Invalid,
}

public sealed record RealTime276Outcome(RealTime276OutcomeKind Kind, string? Edi, string? Message, string? AcceptedContent);

/// <summary>
/// Answers one raw X12 276 in real time: interchange envelope (TA1), parse,
/// claim lookup, 277. An inquiry whose lookup fails is answered E1 (response
/// not possible) on its own; the other inquiries in the 276 still get their
/// status.
/// </summary>
public sealed class RealTime276Service
{
    private readonly ClaimStatusLookup _lookup;
    private readonly IAdjudicationTenantContext _tenantContext;
    private readonly IX12InterchangeIntake? _intake;
    private readonly IOutboundInterchangeTracker? _outboundTracker;
    private readonly TimeProvider _time;
    private readonly ILogger<RealTime276Service> _logger;

    public RealTime276Service(
        ClaimStatusLookup lookup,
        IAdjudicationTenantContext tenantContext,
        ILogger<RealTime276Service> logger,
        IX12InterchangeIntake? intake = null,
        IOutboundInterchangeTracker? outboundTracker = null,
        IOptions<X12InterchangeOptions>? interchangeOptions = null,
        TimeProvider? time = null)
    {
        _lookup = lookup;
        _tenantContext = tenantContext;
        _logger = logger;
        _intake = interchangeOptions?.Value.Enabled == false ? null : intake;
        _outboundTracker = outboundTracker;
        _time = time ?? TimeProvider.System;
    }

    public async Task<RealTime276Outcome> RespondAsync(string tenantId, string edi276, string sourceReference, CancellationToken ct)
    {
        // The claim store reads the tenant from the request's token, or, for a
        // CORE submitter authenticated by certificate or password (no CHO
        // token), from this scope's tenant context, set from its registration.
        _tenantContext.TenantId ??= tenantId;

        InterchangeIntakeResult? envelope = null;
        InterchangeIntakeRequest? intakeRequest = null;
        if (_intake is not null)
        {
            intakeRequest = new InterchangeIntakeRequest { TenantId = tenantId, Content = edi276, TransactionType = "276", RealTime = true };
            envelope = await _intake.ReceiveAsync(intakeRequest, ct);
            if (envelope.IsRejected)
                return new RealTime276Outcome(RealTime276OutcomeKind.EnvelopeRejected, envelope.Ta1, string.Join("; ", envelope.RejectionReasons), null);
            edi276 = envelope.AcceptedContent!;
        }

        Edi276ParseResult parsed;
        try
        {
            parsed = Edi276Parser.Parse(edi276);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to parse 276 EDI");
            if (envelope is not null)
                await _intake!.ReleaseAsync(intakeRequest!, envelope, CancellationToken.None);
            return new RealTime276Outcome(RealTime276OutcomeKind.Invalid, null, ex.Message, edi276);
        }

        var answers = new List<ClaimStatusAnswer>();
        foreach (var inquiry in parsed.Inquiries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                answers.Add(new ClaimStatusAnswer(inquiry, await _lookup.FindAsync(inquiry)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Claim status lookup failed for one 276 inquiry");
                answers.Add(new ClaimStatusAnswer(inquiry, [], Unavailable: true));
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var edi277 = Edi277ClaimStatusGenerator.Generate(parsed, answers, (long)now.TimeOfDay.TotalMilliseconds + 1, now);
        if (_outboundTracker is not null)
            edi277 = await _outboundTracker.GetOrRecordSentAsync(tenantId, edi277, "277", sourceReference, ct);

        _logger.LogInformation("Real-time 276 answered: {Inquiries} inquiries, {Claims} claims found",
            answers.Count, answers.Sum(a => a.Claims.Count));
        return new RealTime276Outcome(RealTime276OutcomeKind.Answered, edi277, null, edi276);
    }

    /// <summary>The 277 sent when no answer is ready within the CORE response budget: E1 for every inquiry.</summary>
    public string? UnableToRespond(string edi276)
    {
        Edi276ParseResult parsed;
        try
        {
            parsed = Edi276Parser.Parse(edi276);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
        var now = _time.GetUtcNow().UtcDateTime;
        return Edi277ClaimStatusGenerator.Generate(parsed,
            parsed.Inquiries.Select(q => new ClaimStatusAnswer(q, [], Unavailable: true)).ToList(),
            (long)now.TimeOfDay.TotalMilliseconds + 1, now);
    }
}

/// <summary>Answers CAQH CORE real-time 276 envelopes at <c>/core/claimstatus</c>.</summary>
public sealed class Core276Handler : ICoreRealTimeHandler
{
    private readonly RealTime276Service _realTime;
    private readonly IOutboundInterchangeTracker? _outboundTracker;
    private readonly TimeProvider _time;

    public Core276Handler(RealTime276Service realTime, IOutboundInterchangeTracker? outboundTracker = null, TimeProvider? time = null)
    {
        _realTime = realTime;
        _outboundTracker = outboundTracker;
        _time = time ?? TimeProvider.System;
    }

    public string RequestPayloadType => CorePayloadTypes.ClaimStatus276;

    public async Task<CorePayloadResult> HandleAsync(CoreRealTimeRequest request, CancellationToken ct)
    {
        var outcome = await _realTime.RespondAsync(request.TenantId, request.Payload, $"core:{request.PayloadId}", ct);
        switch (outcome.Kind)
        {
            case RealTime276OutcomeKind.Answered:
                return new CorePayloadResult(CorePayloadTypes.ClaimStatus277, outcome.Edi!, CorePayloadResult.Outcomes.Answered);
            case RealTime276OutcomeKind.EnvelopeRejected when outcome.Edi is not null:
                return new CorePayloadResult(CorePayloadTypes.AcknowledgmentTa1, outcome.Edi, CorePayloadResult.Outcomes.RejectedTa1);
            case RealTime276OutcomeKind.EnvelopeRejected:
                throw new CorePayloadUnreadableException("The payload has no readable X12 interchange header.");
            default:
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var edi999 = X12999Rejection.Build(outcome.AcceptedContent ?? request.Payload, (long)now.TimeOfDay.TotalMilliseconds + 1, now)
                    ?? throw new CorePayloadUnreadableException("The payload has no readable functional group.");
                if (_outboundTracker is not null)
                    edi999 = await _outboundTracker.GetOrRecordSentAsync(request.TenantId, edi999, "999", $"core:{request.PayloadId}", ct);
                return new CorePayloadResult(CorePayloadTypes.Acknowledgment999, edi999, CorePayloadResult.Outcomes.Rejected999);
            }
        }
    }

    public CorePayloadResult? UnableToRespond(CoreRealTimeRequest request) =>
        _realTime.UnableToRespond(request.Payload) is { } edi
            ? new CorePayloadResult(CorePayloadTypes.ClaimStatus277, edi, CorePayloadResult.Outcomes.UnableToRespond)
            : null;
}
