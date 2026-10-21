using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Middleware;
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

        var contextTenant = HttpContext.GetTenantId();
        if (!string.IsNullOrWhiteSpace(body.TransmissionId)
            && await _transmissions.GetByTenantAndIdAsync(contextTenant, body.TransmissionId, ct) is null)
        {
            return NotFound(new { error = "Transmission not found." });
        }

        // An injected 835 belongs to the caller's tenant: its claims match only
        // that tenant's transmissions, and its remittance and event ids are
        // namespaced by tenant so it can never replay another tenant's receipt.
        body.RestrictToTenantId = contextTenant;
        if (!string.IsNullOrWhiteSpace(body.RemittanceId))
        {
            body.RemittanceId = $"{contextTenant}:{body.RemittanceId.Trim()}";
        }
        if (!string.IsNullOrWhiteSpace(body.EventId))
        {
            body.EventId = $"{contextTenant}:{body.EventId.Trim()}";
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

        var transmission = await _transmissions.GetByTenantAndIdAsync(HttpContext.GetTenantId(), transmissionId, ct);
        if (transmission is null)
        {
            return NotFound(new { error = "Transmission not found." });
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

        var tenantId = HttpContext.GetTenantId();

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
