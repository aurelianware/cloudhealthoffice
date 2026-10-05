using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only demo for outbound 837 claim submission through the
/// vendor-neutral healthcare transaction gateway. Disabled outside Development.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayClaimDemoController : ControllerBase
{
    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IHostEnvironment _environment;

    public GatewayClaimDemoController(
        IHealthcareGatewayResolver resolver,
        IHostEnvironment environment)
    {
        _resolver = resolver;
        _environment = environment;
    }

    [HttpPost("claims")]
    public async Task<IActionResult> Submit(
        [FromBody] GatewayClaimSubmissionRequest request,
        [FromQuery] string? gateway,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        // The tenant is the caller's token tenant; a tenantId in the body is ignored.
        request.TenantId = HttpContext.GetTenantId();

        IClaimSubmissionGateway submission;
        try
        {
            submission = _resolver.ResolveCapability<IClaimSubmissionGateway>(gateway);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var result = await submission.SubmitClaimAsync(request, ct);
        return Ok(result);
    }
}
