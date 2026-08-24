using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only 276/277 claim status inquiry against an existing claim
/// transmission. Disabled outside Development. Tenant is taken from
/// X-Tenant-ID / the original transmission — never from an unauthenticated
/// production route.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayClaimStatusDemoController : ControllerBase
{
    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimStatusInquiryStore _inquiries;
    private readonly IHostEnvironment _environment;

    public GatewayClaimStatusDemoController(
        IHealthcareGatewayResolver resolver,
        IClaimTransmissionStore transmissions,
        IClaimStatusInquiryStore inquiries,
        IHostEnvironment environment)
    {
        _resolver = resolver;
        _transmissions = transmissions;
        _inquiries = inquiries;
        _environment = environment;
    }

    [HttpPost("claims/{transmissionId}/status")]
    public async Task<IActionResult> Check(
        string transmissionId,
        [FromQuery] string? gateway,
        [FromQuery] int? serviceLineNumber,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var transmission = await LoadTransmissionForTenantAsync(transmissionId, ct);
        if (transmission.Error is not null)
        {
            return transmission.Error;
        }

        IClaimStatusGateway status;
        try
        {
            status = _resolver.ResolveCapability<IClaimStatusGateway>(gateway);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var record = transmission.Record!;
        var result = await status.CheckClaimStatusAsync(
            new ClaimStatusRequest
            {
                TenantId = record.TenantId,
                ClaimId = record.ClaimId,
                TransmissionId = record.TransmissionId,
                PayerId = record.PayerId,
                ServiceLineNumber = serviceLineNumber,
                CorrelationId = record.CorrelationId
            },
            ct);
        return Ok(result);
    }

    [HttpGet("claims/{transmissionId}/status")]
    public async Task<IActionResult> History(string transmissionId, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var transmission = await LoadTransmissionForTenantAsync(transmissionId, ct);
        if (transmission.Error is not null)
        {
            return transmission.Error;
        }

        var list = await _inquiries.ListByTransmissionIdAsync(transmission.Record!.TransmissionId, ct);
        return Ok(list);
    }

    private async Task<(ClaimTransmissionRecord? Record, IActionResult? Error)> LoadTransmissionForTenantAsync(
        string transmissionId, CancellationToken ct)
    {
        var transmission = await _transmissions.GetByIdAsync(transmissionId, ct);
        if (transmission is null)
        {
            return (null, NotFound(new { error = "Transmission not found." }));
        }

        var contextTenant = HttpContext.Items["TenantId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(contextTenant) &&
            !string.Equals(contextTenant, transmission.TenantId, StringComparison.Ordinal))
        {
            return (null, BadRequest(new { error = "Tenant does not match the claim transmission." }));
        }

        return (transmission, null);
    }
}
