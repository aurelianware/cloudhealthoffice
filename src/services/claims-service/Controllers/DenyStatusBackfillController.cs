using ClaimsService.Services.Migrations;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaimsService.Controllers;

/// <summary>
/// Operator endpoint for the one-time Pay/Deny lifecycle backfill (tenant
/// admins, <c>settings:manage</c>; opt-in via
/// <c>Claims:DenyStatusBackfill:Enabled</c>, 503 otherwise). Runs for the
/// request's tenant (X-Tenant-ID). See
/// docs/operations/CLAIM-DENY-STATUS-BACKFILL.md.
/// </summary>
[ApiController]
[Route("api/claims/admin/lifecycle")]
[RequirePermission(AccumulatorOperationsController.SettingsManagePermission)]
public sealed class DenyStatusBackfillController : ControllerBase
{
    private readonly DenyStatusBackfillService _backfill;
    private readonly IOptionsMonitor<DenyStatusBackfillOptions> _options;
    private readonly ICurrentActor _actor;

    public DenyStatusBackfillController(
        DenyStatusBackfillService backfill, IOptionsMonitor<DenyStatusBackfillOptions> options, ICurrentActor actor)
    {
        _backfill = backfill;
        _options = options;
        _actor = actor;
    }

    /// <summary>
    /// Moves claims Denied at adjudication (before the Deny status existed)
    /// with no payment-cycle evidence to Deny. Dry-run by default
    /// (<c>dryRun: false</c> writes); <c>remittedClaimIds</c> (from
    /// payment-service's 835 envelopes) stay Denied. Idempotent: a rerun
    /// finds nothing left to move.
    /// </summary>
    [HttpPost("deny-backfill")]
    [ProducesResponseType(typeof(DenyStatusBackfillResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DenyStatusBackfillResult>> Run([FromBody] DenyStatusBackfillRequest? request)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "deny_backfill_disabled",
                message = "The Deny status backfill is opt-in. Set Claims:DenyStatusBackfill:Enabled=true, " +
                          "run it dry first, and follow docs/operations/CLAIM-DENY-STATUS-BACKFILL.md.",
            });
        }

        var tenantId = HttpContext.Items["TenantId"]?.ToString()
                       ?? throw new InvalidOperationException("TenantId not found in HttpContext.");
        return Ok(await _backfill.RunAsync(
            tenantId, request ?? new DenyStatusBackfillRequest(), _actor.UserId, HttpContext.RequestAborted));
    }
}
