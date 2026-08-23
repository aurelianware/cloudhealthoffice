using CloudHealthOffice.Infrastructure.Responders;
using EligibilityService.Adapters;
using EligibilityService.Services;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only raw X12 270 ingress that uses the existing
/// <see cref="IEdi270Parser"/> / <see cref="IEdi271Generator"/> without
/// persisting the inquiry. Production hosts must not expose this route.
/// </summary>
[ApiController]
[Route("api/dev/payer")]
public sealed class PayerEligibilityX12DevController : ControllerBase
{
    private readonly IEdi270Parser _parser;
    private readonly IEdi271Generator _generator;
    private readonly IEligibilityResponder _responder;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PayerEligibilityX12DevController> _logger;

    public PayerEligibilityX12DevController(
        IEdi270Parser parser,
        IEdi271Generator generator,
        IEligibilityResponder responder,
        IHostEnvironment environment,
        ILogger<PayerEligibilityX12DevController> logger)
    {
        _parser = parser;
        _generator = generator;
        _responder = responder;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Accept raw X12 270 text and return raw X12 271 text via the canonical
    /// payer responder. Does not call <c>ProcessInquiryAsync</c> and does not
    /// persist inquiries or mutate accumulators.
    /// </summary>
    [HttpPost("eligibility/x12")]
    [Consumes("text/plain")]
    [Produces("text/plain")]
    public async Task<IActionResult> RespondX12(CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        string edi270;
        using (var reader = new StreamReader(Request.Body))
        {
            edi270 = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(edi270))
        {
            return BadRequest("Request body must contain the raw X12 270 EDI string.");
        }

        Edi270ParseResult parsed;
        try
        {
            parsed = _parser.Parse(edi270);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse inbound 270 EDI. Transaction={TransactionType} Adapter={Adapter}",
                "Eligibility270271", "x12");
            return BadRequest("Invalid 270 EDI.");
        }

        var inquiry = X12PayerEligibilityMapper.ToInquiry(parsed);
        var envelope = await _responder.RespondAsync(inquiry, ct);

        if (!envelope.IsSuccess || envelope.Result is null)
        {
            _logger.LogWarning(
                "Inbound X12 eligibility transport failed. Transaction={TransactionType} Adapter={Adapter} ErrorCategory={ErrorCategory}",
                "Eligibility270271",
                "x12",
                envelope.Metadata.ErrorCategory);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to respond.");
        }

        var serviceResponse = X12PayerEligibilityMapper.ToServiceResponse(parsed.Inquiry, envelope.Result);
        var edi271 = _generator.Generate(
            parsed.Inquiry,
            serviceResponse,
            isaSenderId: parsed.InterchangeReceiverId,
            isaReceiverId: parsed.InterchangeSenderId);

        _logger.LogInformation(
            "Dev X12 payer eligibility completed. Transaction={TransactionType} CorrelationId={CorrelationId} Business={Business} Adapter={Adapter}",
            "Eligibility270271",
            SanitizeForLog(inquiry.CorrelationId ?? inquiry.TransactionId),
            envelope.Result.BusinessStatus,
            "x12");

        return Content(edi271, "text/plain");
    }

    private static string? SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
