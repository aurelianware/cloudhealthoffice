using BenefitPlanService.Middleware;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// The change history of this tenant's fee schedules: one entry for every
/// create and replace, whichever importer, library deploy or API made it, with
/// the codes whose rates moved. Retro rate reprocessing starts from here. See
/// docs/integration/RETRO-RATE-REPROCESSING.md.
/// </summary>
[ApiController]
public sealed class FeeScheduleChangesController : ControllerBase
{
    private readonly IFeeScheduleHistoryRepository _history;

    public FeeScheduleChangesController(IFeeScheduleHistoryRepository history) => _history = history;

    /// <summary>Changes newest first, optionally for one schedule (<c>scheduleId</c>) and since a time.</summary>
    [HttpGet("api/v1/fee-schedules/changes")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(IReadOnlyList<FeeScheduleChange>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FeeScheduleChange>>> List(
        [FromQuery] string? scheduleId, [FromQuery] DateTime? since, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });
        return Ok(await _history.ListChangesAsync(tenantId, scheduleId, since, limit, ct));
    }

    /// <summary>
    /// A schedule as it stood at an earlier revision (the rates a claim line
    /// with that <c>feeScheduleRevision</c> was priced on), with a page of its
    /// rates filtered by code prefix. 404 when that revision was never
    /// replaced: the live schedule still holds it.
    /// </summary>
    [HttpGet("api/v1/fee-schedules/revision")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(FeeScheduleRevisionSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeeScheduleRevisionSnapshot>> GetRevision(
        [FromQuery] string id, [FromQuery] long revision, [FromQuery] string? code,
        [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { message = "id is required." });

        var snapshot = await _history.GetRevisionAsync(tenantId, id, revision, ct);
        if (snapshot is null)
            return NotFound(new { message = $"No superseded revision {revision} of schedule '{id}'; the live schedule holds it." });

        // A page of lines, like the schedule detail endpoints, never all 10,000.
        var lines = snapshot.Schedule.Lines
            .Where(l => string.IsNullOrWhiteSpace(code)
                || l.ProcedureCode.StartsWith(code.Trim(), StringComparison.OrdinalIgnoreCase))
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .ToList();
        snapshot.Schedule.Lines = lines;
        return Ok(snapshot);
    }
}
