using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Logging;
using EligibilityService.Models;
using Microsoft.Extensions.Options;

namespace EligibilityService.Services;

public enum RealTime270OutcomeKind
{
    /// <summary>A 271 was produced.</summary>
    Answered,

    /// <summary>The ISA/IEA envelope was rejected: <see cref="RealTime270Outcome.Edi"/> is the TA1, when one could be built.</summary>
    EnvelopeRejected,

    /// <summary>The envelope was accepted but the 270 could not be parsed.</summary>
    Invalid,

    /// <summary>Coverage could not be checked right now (a dependency is down).</summary>
    Unavailable,

    /// <summary>Unexpected failure while answering.</summary>
    Failed,
}

public sealed class RealTime270Outcome
{
    public required RealTime270OutcomeKind Kind { get; init; }

    /// <summary>The 271 (Answered) or TA1 (EnvelopeRejected).</summary>
    public string? Edi { get; init; }

    /// <summary>A reason for the submitter (Invalid, EnvelopeRejected without a TA1). No member data.</summary>
    public string? Message { get; init; }

    /// <summary>The accepted 270 content (after envelope intake), for a 999.</summary>
    public string? AcceptedContent { get; init; }

    public Edi270ParseResult? Parsed { get; init; }
    public EligibilityResponse? Response { get; init; }
    public IReadOnlyList<string> AcknowledgmentIds { get; init; } = [];
}

/// <summary>
/// Answers one raw X12 270 in real time: interchange envelope (TA1), parse,
/// eligibility, 271. Shared by <c>POST api/eligibility/270</c> and the CAQH
/// CORE endpoint, which differ only in how they are called and how they
/// return the result.
/// </summary>
public interface IRealTime270Service
{
    Task<RealTime270Outcome> RespondAsync(string tenantId, string edi270, CancellationToken ct);
}

public sealed class RealTime270Service : IRealTime270Service
{
    private readonly IEligibilityService _eligibilityService;
    private readonly IEdi270Parser _parser;
    private readonly IEdi271Generator _generator;
    private readonly IX12InterchangeIntake? _intake;
    private readonly IOutboundInterchangeTracker? _outboundTracker;
    private readonly ILogger<RealTime270Service> _logger;

    public RealTime270Service(
        IEligibilityService eligibilityService,
        IEdi270Parser parser,
        IEdi271Generator generator,
        ILogger<RealTime270Service> logger,
        IX12InterchangeIntake? intake = null,
        IOutboundInterchangeTracker? outboundTracker = null,
        IOptions<X12InterchangeOptions>? interchangeOptions = null)
    {
        _eligibilityService = eligibilityService;
        _parser = parser;
        _generator = generator;
        _logger = logger;
        _intake = interchangeOptions?.Value.Enabled == false ? null : intake;
        _outboundTracker = outboundTracker;
    }

    public async Task<RealTime270Outcome> RespondAsync(string tenantId, string edi270, CancellationToken ct)
    {
        // Interchange envelope first. A rejected ISA/IEA is answered with the
        // TA1 alone: no 271 and no 999 for its contents.
        InterchangeIntakeResult? envelope = null;
        InterchangeIntakeRequest? intakeRequest = null;
        if (_intake is not null)
        {
            // Real time: the duplicate-ISA13 check is off by default for this
            // path (X12Interchange:DuplicateCheck:270-realtime), so a retry
            // after a timeout still gets its 271.
            intakeRequest = new InterchangeIntakeRequest
            {
                TenantId = tenantId,
                Content = edi270,
                TransactionType = "270",
                RealTime = true,
            };
            envelope = await _intake.ReceiveAsync(intakeRequest, ct);

            if (envelope.IsRejected)
            {
                _logger.LogWarning("EDI 270 rejected at the interchange envelope");
                return new RealTime270Outcome
                {
                    Kind = RealTime270OutcomeKind.EnvelopeRejected,
                    Edi = envelope.Ta1,
                    Message = string.Join("; ", envelope.RejectionReasons),
                    AcknowledgmentIds = envelope.AcknowledgmentIds,
                };
            }
            edi270 = envelope.AcceptedContent!;
        }

        RealTime270Outcome outcome;
        try
        {
            outcome = await AnswerAcceptedAsync(tenantId, edi270, ct);
        }
        catch when (envelope is not null)
        {
            await _intake!.ReleaseAsync(intakeRequest!, envelope, CancellationToken.None);
            throw;
        }

        // A 270 that got no 271 frees its control number for the resubmission.
        if (outcome.Kind != RealTime270OutcomeKind.Answered && envelope is not null)
            await _intake!.ReleaseAsync(intakeRequest!, envelope, CancellationToken.None);

        return new RealTime270Outcome
        {
            Kind = outcome.Kind,
            Edi = outcome.Edi,
            Message = outcome.Message,
            AcceptedContent = edi270,
            Parsed = outcome.Parsed,
            Response = outcome.Response,
            AcknowledgmentIds = envelope?.AcknowledgmentIds ?? [],
        };
    }

    private async Task<RealTime270Outcome> AnswerAcceptedAsync(string tenantId, string edi270, CancellationToken ct)
    {
        Edi270ParseResult parsed;
        try
        {
            parsed = _parser.Parse(edi270);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // The parser's own validation messages describe the input, not the server.
            _logger.LogWarning(ex, "Failed to parse 270 EDI");
            return new RealTime270Outcome { Kind = RealTime270OutcomeKind.Invalid, Message = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse 270 EDI");
            return new RealTime270Outcome { Kind = RealTime270OutcomeKind.Invalid, Message = null };
        }

        var inquiry = parsed.Inquiry;
        inquiry.TenantId = tenantId;
        inquiry.ControlNumber = string.IsNullOrEmpty(inquiry.ControlNumber)
            ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
            : inquiry.ControlNumber;

        _logger.LogInformation(
            "Processing EDI 270 for subscriber {SubscriberId}, serviceType={ServiceType}",
            LogSanitizer.MaskId(inquiry.SubscriberId), inquiry.ServiceTypeCode);

        EligibilityResponse response;
        try
        {
            response = await _eligibilityService.ProcessInquiryAsync(inquiry);
        }
        catch (CoverageLookupUnavailableException ex)
        {
            _logger.LogWarning(ex, "Coverage verification unavailable");
            return new RealTime270Outcome { Kind = RealTime270OutcomeKind.Unavailable, Parsed = parsed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing eligibility inquiry from 270 EDI");
            return new RealTime270Outcome { Kind = RealTime270OutcomeKind.Failed, Parsed = parsed };
        }

        // Swap ISA sender/receiver so the 271 flows back to the submitter
        var edi271 = _generator.Generate(
            inquiry, response,
            isaSenderId:   parsed.InterchangeReceiverId, // 270's receiver = 271's sender (payer)
            isaReceiverId: parsed.InterchangeSenderId);  // 270's sender  = 271's receiver (provider)

        // Sequenced ISA13/GS06/ST02, recorded so the submitter's TA1 can be
        // matched; a later download of this response reuses them.
        if (_outboundTracker is not null)
            edi271 = await _outboundTracker.GetOrRecordSentAsync(tenantId, edi271, "271", response.Id, ct);

        return new RealTime270Outcome
        {
            Kind = RealTime270OutcomeKind.Answered,
            Edi = edi271,
            Parsed = parsed,
            Response = response,
        };
    }
}
