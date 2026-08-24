using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only 277CA injection and read surface. Feeds the same
/// <see cref="IClaimAcknowledgmentProcessor"/> used by the Stedi adapter.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayClaimAcknowledgmentDemoController : ControllerBase
{
    private readonly IClaimAcknowledgmentProcessor _processor;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAcknowledgmentStore _acknowledgments;
    private readonly IHostEnvironment _environment;

    public GatewayClaimAcknowledgmentDemoController(
        IClaimAcknowledgmentProcessor processor,
        IClaimTransmissionStore transmissions,
        IClaimAcknowledgmentStore acknowledgments,
        IHostEnvironment environment)
    {
        _processor = processor;
        _transmissions = transmissions;
        _acknowledgments = acknowledgments;
        _environment = environment;
    }

    [HttpPost("claims/{transmissionId}/277ca")]
    public async Task<IActionResult> Inject(
        string transmissionId,
        [FromBody] GatewayClaimAcknowledgmentInjection body,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var transmission = await _transmissions.GetByIdAsync(transmissionId, ct);
        if (transmission is null)
        {
            return NotFound(new { error = "Transmission not found." });
        }

        var ack = new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = string.IsNullOrWhiteSpace(body.AcknowledgmentId)
                ? $"dev-{transmission.TransmissionId}"
                : body.AcknowledgmentId.Trim(),
            Gateway = string.IsNullOrWhiteSpace(transmission.GatewayName)
                ? "Mock"
                : transmission.GatewayName,
            TransmissionId = transmission.TransmissionId,
            OriginalSubmissionId = body.OriginalSubmissionId ?? transmission.SubmissionId,
            ClaimId = transmission.ClaimId,
            ClaimType = transmission.ClaimType,
            ReceivedAt = DateTimeOffset.UtcNow,
            Status = body.Status,
            ClaimControlNumber = body.ClaimControlNumber,
            PatientControlNumber = transmission.PatientControlNumber ?? transmission.ClaimId,
            Errors = body.Errors ?? new(),
            ServiceLineResults = body.ServiceLineResults ?? new()
        };

        var result = await _processor.ProcessAsync(ack, ct);
        return Ok(result);
    }

    [HttpGet("transmissions/{transmissionId}")]
    public async Task<IActionResult> GetTransmission(string transmissionId, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var transmission = await _transmissions.GetByIdAsync(transmissionId, ct);
        return transmission is null ? NotFound() : Ok(transmission);
    }

    [HttpGet("acknowledgments")]
    public async Task<IActionResult> ListAcknowledgments(
        [FromQuery] string transmissionId,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(transmissionId))
        {
            return BadRequest(new { error = "transmissionId is required." });
        }

        var list = await _acknowledgments.ListByTransmissionIdAsync(transmissionId, ct);
        return Ok(list);
    }
}
