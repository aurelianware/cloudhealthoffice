using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only direct ingress for the payer-side eligibility responder.
/// Accepts the canonical <see cref="PayerEligibilityInquiry"/> and returns the
/// canonical <see cref="PayerEligibilityResponse"/>.
///
/// Disabled outside the Development environment (404). Not a production
/// clearinghouse endpoint; production ingress requires a trusted adapter
/// (API key / OAuth2 / mTLS / signed webhook) once a network contract exists.
/// </summary>
[ApiController]
[Route("api/dev/payer")]
public sealed class PayerEligibilityDevController : ControllerBase
{
    private readonly ICanonicalInboundEligibilityAdapter _adapter;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PayerEligibilityDevController> _logger;

    public PayerEligibilityDevController(
        ICanonicalInboundEligibilityAdapter adapter,
        IHostEnvironment environment,
        ILogger<PayerEligibilityDevController> logger)
    {
        _adapter = adapter;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Run a canonical 270-equivalent inquiry through Cloud Health Office as
    /// the payer. HTTP 200 is a transport success even when the business
    /// status is a rejection (invalid subscriber, member not found, etc.).
    /// </summary>
    [HttpPost("eligibility")]
    public async Task<IActionResult> Respond(
        [FromBody] PayerEligibilityInquiry inquiry,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        inquiry.AdapterName ??= CanonicalInboundEligibilityAdapter.AdapterName;

        var envelope = await _adapter.ProcessAsync(inquiry, ct);

        _logger.LogInformation(
            "Dev payer eligibility completed. Transaction={TransactionType} CorrelationId={CorrelationId} TransportSuccess={TransportSuccess} Adapter={Adapter}",
            "Eligibility270271",
            SanitizeForLog(inquiry.CorrelationId ?? inquiry.TransactionId),
            envelope.IsSuccess,
            _adapter.Name);

        if (!envelope.IsSuccess)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, envelope);
        }

        return Ok(envelope);
    }

    private static string? SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
