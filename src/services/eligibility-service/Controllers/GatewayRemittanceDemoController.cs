using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only 835 remittance injection, read, and posting surface.
/// Inject/list feed <see cref="IRemittanceProcessor"/>; post uses
/// <see cref="IRemittancePoster"/>. Disabled outside Development.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayRemittanceDemoController : ControllerBase
{
    private readonly IRemittanceProcessor _processor;
    private readonly IRemittancePoster _poster;
    private readonly IRemittanceStore _receipts;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IHostEnvironment _environment;

    public GatewayRemittanceDemoController(
        IRemittanceProcessor processor,
        IRemittancePoster poster,
        IRemittanceStore receipts,
        IClaimTransmissionStore transmissions,
        IHostEnvironment environment)
    {
        _processor = processor;
        _poster = poster;
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

    [HttpPost("remittance/{receiptId}/post")]
    public async Task<IActionResult> Post(string receiptId, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var tenantId = HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant is required." });
        }

        var result = await _poster.PostAsync(
            new RemittancePostRequest { ReceiptId = receiptId, TenantId = tenantId },
            ct);
        if (result.ErrorCategory != GatewayErrorCategory.None &&
            result.Status != RemittanceLifecycleStatus.Posted)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
