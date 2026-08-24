using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only demo endpoint that runs a canonical eligibility request
/// through the vendor-neutral healthcare transaction gateway, so the same
/// request can be pointed at the Mock gateway or Stedi purely by configuration
/// (<c>HealthcareTransactions:DefaultGateway</c>) or the optional
/// <c>?gateway=</c> selector.
///
/// It exists to demonstrate the gateway abstraction end to end using an existing
/// service — it is not part of the production API and is disabled outside the
/// Development environment.
/// </summary>
[ApiController]
[Route("api/gateway-demo")]
public class GatewayEligibilityDemoController : ControllerBase
{
    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IHostEnvironment _environment;

    /// <summary>Populated by the tenant action filter from the request context.</summary>
    public string TenantId { get; set; } = string.Empty;

    public GatewayEligibilityDemoController(
        IHealthcareGatewayResolver resolver,
        IHostEnvironment environment)
    {
        _resolver = resolver;
        _environment = environment;
    }

    /// <summary>
    /// Run an eligibility check through the configured (or named) gateway.
    /// </summary>
    /// <param name="request">
    /// Canonical eligibility request. For a dependent inquiry set
    /// <c>Subscriber</c> (or the flat subscriber fields) and <c>Patient</c>.
    /// </param>
    /// <param name="gateway">Optional gateway name (e.g. "Mock" or "Stedi").</param>
    [HttpPost("eligibility")]
    public async Task<IActionResult> CheckEligibility(
        [FromBody] GatewayEligibilityRequest request,
        [FromQuery] string? gateway,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            request.TenantId = TenantId;
        }

        IEligibilityGateway eligibility;
        try
        {
            eligibility = _resolver.ResolveCapability<IEligibilityGateway>(gateway);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var result = await eligibility.CheckEligibilityAsync(request, ct);
        return Ok(result);
    }
}
