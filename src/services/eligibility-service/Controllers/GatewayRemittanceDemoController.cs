using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only 835 remittance injection and read surface. Feeds the same
/// <see cref="IRemittanceProcessor"/> used by the Stedi adapter. Disabled
/// outside Development.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayRemittanceDemoController : ControllerBase
{
    private readonly IRemittanceProcessor _processor;
    private readonly IRemittanceStore _receipts;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IHostEnvironment _environment;

    public GatewayRemittanceDemoController(
        IRemittanceProcessor processor,
        IRemittanceStore receipts,
        IClaimTransmissionStore transmissions,
        IHostEnvironment environment)
    {
        _processor = processor;
        _receipts = receipts;
        _transmissions = transmissions;
        _environment = environment;
    }

    [HttpPost("remittance")]
    public async Task<IActionResult> Inject(
        [FromBody] GatewayRemittance body,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var contextTenant = HttpContext.Items["TenantId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(body.TransmissionId))
        {
            var transmission = await _transmissions.GetByIdAsync(body.TransmissionId, ct);
            if (transmission is null)
            {
                return NotFound(new { error = "Transmission not found." });
            }

            if (!string.IsNullOrWhiteSpace(contextTenant) &&
                !string.Equals(contextTenant, transmission.TenantId, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "Tenant does not match the claim transmission." });
            }
        }

        if (string.IsNullOrWhiteSpace(body.Gateway))
        {
            body.Gateway = "Mock";
        }

        if (body.ReceivedAt == default)
        {
            body.ReceivedAt = DateTimeOffset.UtcNow;
        }

        var result = await _processor.ProcessAsync(body, ct);
        return Ok(result);
    }

    [HttpGet("claims/{transmissionId}/remittance")]
    public async Task<IActionResult> List(string transmissionId, CancellationToken ct)
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

        var contextTenant = HttpContext.Items["TenantId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(contextTenant) &&
            !string.Equals(contextTenant, transmission.TenantId, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Tenant does not match the claim transmission." });
        }

        var list = await _receipts.ListByTransmissionIdAsync(transmission.TransmissionId, ct);
        return Ok(list);
    }
}
