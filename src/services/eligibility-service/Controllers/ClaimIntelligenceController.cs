using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Read-only claim intelligence API. Composes 837 / 277CA / 276/277 / 275 / 835
/// into a tenant-scoped workflow view for CDO, a future provider portal, and
/// operations. Does not post payment or mutate transaction stores.
/// </summary>
[ApiController]
[Route("api/claims")]
[Produces("application/json")]
public sealed class ClaimIntelligenceController : ControllerBase
{
    private readonly IClaimIntelligenceComposer _composer;

    public ClaimIntelligenceController(IClaimIntelligenceComposer composer)
    {
        _composer = composer;
    }

    [HttpGet("{claimId}/intelligence")]
    [ProducesResponseType(typeof(ClaimIntelligenceView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string claimId, CancellationToken ct)
    {
        var tenantId = HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant is required." });
        }

        if (string.IsNullOrWhiteSpace(claimId))
        {
            return BadRequest(new { error = "Claim id is required." });
        }

        var view = await _composer.ComposeAsync(
            new ClaimIntelligenceRequest { TenantId = tenantId, ClaimId = claimId },
            ct);
        if (view is null)
        {
            return NotFound(new { error = "Claim intelligence not found." });
        }

        return Ok(view);
    }
}
