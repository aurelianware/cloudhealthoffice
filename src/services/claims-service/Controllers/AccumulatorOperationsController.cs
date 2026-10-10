using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Operator endpoints for the engine-accumulator outbox rollout (tenant
/// admins, <c>settings:manage</c>). See claim-cob-pipeline.md, deploy notes.
/// </summary>
[ApiController]
[Route("api/claims/admin/accumulators")]
[RequirePermission(SettingsManagePermission)]
public sealed class AccumulatorOperationsController : ControllerBase
{
    public const string SettingsManagePermission = "settings:manage";
    public const int MaxSweepClaims = 5000;
    public const int MaxRerunClaims = 500;

    private readonly AccumulatorOperationsService _operations;
    private readonly ICurrentActor _actor;

    public AccumulatorOperationsController(AccumulatorOperationsService operations, ICurrentActor actor)
    {
        _operations = operations;
        _actor = actor;
    }

    /// <summary>
    /// Read-only sweep: finalized claims (Approved / PartiallyPaid / Paid,
    /// not superseded) last updated in [from, to] with a non-zero cost share,
    /// no pending accumulator entry, and no write record in the engine store.
    /// Run after the rollout over its window; re-adjudicate or correct what
    /// it lists.
    /// </summary>
    [HttpGet("missing-commits")]
    [ProducesResponseType(typeof(AccumulatorSweepResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccumulatorSweepResult>> MissingCommits(
        [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] int limit = 1000)
    {
        if (from == default || to == default || to < from)
            return BadRequest("from and to (UTC) are required, with from <= to");
        return Ok(await _operations.FindMissingCommitsAsync(
            DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc),
            Math.Clamp(limit, 1, MaxSweepClaims), HttpContext.RequestAborted));
    }

    /// <summary>
    /// Re-runs claims pended <c>ACCUMCOMMIT</c> (claims-service deployed
    /// before benefit-plan-service). Each passes only on a clean
    /// re-adjudication; nothing is overridden.
    /// </summary>
    [HttpPost("accumulator-commit-pends/rerun")]
    [ProducesResponseType(typeof(AccumulatorPendRerunResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccumulatorPendRerunResult>> RerunAccumulatorCommitPends([FromQuery] int limit = 100)
    {
        var tenantId = HttpContext.Items["TenantId"]?.ToString()
                       ?? throw new InvalidOperationException("TenantId not found in HttpContext.");
        return Ok(await _operations.RerunAccumulatorCommitPendsAsync(
            tenantId, _actor.UserId, Math.Clamp(limit, 1, MaxRerunClaims), HttpContext.RequestAborted));
    }
}
