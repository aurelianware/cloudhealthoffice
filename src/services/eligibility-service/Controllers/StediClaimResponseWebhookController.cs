using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EligibilityService.Controllers;

/// <summary>
/// Production webhook for Stedi <c>transaction.processed.v2</c> claim-response
/// events. The body is a pointer; 277CA content is retrieved through
/// <see cref="IClaimAcknowledgmentIngress"/>.
///
/// Stedi authenticates with a configured API-key header (credential set). It
/// does not HMAC-sign these webhooks.
/// </summary>
[ApiController]
[Route("api/integrations/stedi")]
public sealed class StediClaimResponseWebhookController : ControllerBase
{
    private readonly IClaimAcknowledgmentIngress _ingress;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediClaimResponseWebhookController> _logger;

    public StediClaimResponseWebhookController(
        IClaimAcknowledgmentIngress ingress,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimResponseWebhookController> logger)
    {
        _ingress = ingress;
        _options = options;
        _logger = logger;
    }

    [HttpPost("claim-responses")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var opts = _options.Value;
        var headerName = string.IsNullOrWhiteSpace(opts.WebhookCredentialHeaderName)
            ? "Authorization"
            : opts.WebhookCredentialHeaderName;
        var provided = Request.Headers[headerName].ToString();
        if (!opts.WebhookCredentialIsValid(provided))
        {
            return Unauthorized();
        }

        var limit = opts.WebhookMaxPayloadBytes <= 0 ? 65536 : opts.WebhookMaxPayloadBytes;
        if (Request.ContentLength is > 0 && Request.ContentLength > limit)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        string json;
        using (var reader = new StreamReader(Request.Body))
        {
            json = await reader.ReadToEndAsync(ct);
        }

        if (json.Length > limit)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!StediHealthcareGateway.TryParseClaimResponseEvent(json, out var discovery))
        {
            _logger.LogWarning("Stedi claim-response webhook body could not be parsed");
            return BadRequest(new { error = "Malformed webhook event." });
        }

        var result = await _ingress.IngestDiscoveredAsync(discovery, ct);
        if (result.TransientFailure)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = result.ErrorMessage,
                category = result.ErrorCategory.ToString()
            });
        }

        return Ok(new
        {
            ignored = result.Ignored,
            replay = result.Replay,
            processed = result.Processed,
            status = result.Status?.ToString(),
            acknowledgmentId = result.AcknowledgmentId,
            transmissionId = result.TransmissionId
        });
    }
}
