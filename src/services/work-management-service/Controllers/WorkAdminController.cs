using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WorkManagementService.Models;
using WorkManagementService.Services;

namespace WorkManagementService.Controllers;

/// <summary>
/// Work-management configuration (<c>workqueue:admin</c>): queues (criteria,
/// order, SLA, weights, active), teams, memberships, effective queues per
/// user, and the recompute / reconcile operations. Changing a queue does not
/// move existing items by itself: run recompute.
/// </summary>
[ApiController]
[Route("api/v1/work/admin")]
[Produces("application/json")]
[RequirePermission(ChoRolePermissions.WorkQueueAdmin)]
public sealed class WorkAdminController : ControllerBase
{
    private readonly WorkConfigService _config;
    private readonly WorkIntakeService _intake;
    private readonly IClaimsWorkSource _claims;
    private readonly IEnrollmentWorkSource _enrollment;
    private readonly WorkManagementOptions _options;
    private readonly ICurrentActor _actor;

    public WorkAdminController(
        WorkConfigService config, WorkIntakeService intake, IClaimsWorkSource claims, IEnrollmentWorkSource enrollment,
        IOptions<WorkManagementOptions> options, ICurrentActor actor)
    {
        _enrollment = enrollment;
        _config = config;
        _intake = intake;
        _claims = claims;
        _options = options.Value;
        _actor = actor;
    }

    private string Tenant => _actor.TenantId;

    private ActionResult Result<T>(ConfigResult<T> result) => result.Status switch
    {
        200 => Ok(result.Value),
        404 => NotFound(new { error = result.Error }),
        409 => Conflict(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };

    // ── Queues ───────────────────────────────────────────────────────────

    [HttpGet("queues")]
    public async Task<ActionResult<IReadOnlyList<WorkQueue>>> Queues(CancellationToken ct) =>
        Ok(await _config.ListQueuesAsync(Tenant, ct));

    [HttpPost("queues")]
    public async Task<ActionResult> CreateQueue([FromBody] QueueInput input, CancellationToken ct) =>
        Result(await _config.CreateQueueAsync(Tenant, input, _actor.UserId, ct));

    [HttpPut("queues/{id}")]
    public async Task<ActionResult> UpdateQueue(string id, [FromBody] QueueInput input, CancellationToken ct) =>
        Result(await _config.UpdateQueueAsync(Tenant, id, input, _actor.UserId, ct));

    [HttpDelete("queues/{id}")]
    public async Task<ActionResult> DeleteQueue(string id, CancellationToken ct) =>
        Result(await _config.DeleteQueueAsync(Tenant, id, ct));

    /// <summary>Sets the routing order of a work type's (non-fallback) queues.</summary>
    [HttpPost("queues/reorder")]
    public async Task<ActionResult> Reorder([FromBody] ReorderRequest request, CancellationToken ct) =>
        Result(await _config.ReorderAsync(Tenant, request.WorkType, request.QueueIds ?? new(), _actor.UserId, ct));

    // ── Teams ────────────────────────────────────────────────────────────

    [HttpGet("teams")]
    public async Task<ActionResult<IReadOnlyList<WorkTeam>>> Teams(CancellationToken ct) =>
        Ok(await _config.ListTeamsAsync(Tenant, ct));

    [HttpPost("teams")]
    public async Task<ActionResult> CreateTeam([FromBody] TeamInput input, CancellationToken ct) =>
        Result(await _config.SaveTeamAsync(Tenant, null, input, _actor.UserId, ct));

    [HttpPut("teams/{id}")]
    public async Task<ActionResult> UpdateTeam(string id, [FromBody] TeamInput input, CancellationToken ct) =>
        Result(await _config.SaveTeamAsync(Tenant, id, input, _actor.UserId, ct));

    [HttpDelete("teams/{id}")]
    public async Task<ActionResult> DeleteTeam(string id, CancellationToken ct) =>
        Result(await _config.DeleteTeamAsync(Tenant, id, ct));

    // ── Memberships ──────────────────────────────────────────────────────

    [HttpGet("memberships")]
    public async Task<ActionResult<IReadOnlyList<WorkMembership>>> Memberships(CancellationToken ct) =>
        Ok(await _config.ListMembershipsAsync(Tenant, ct));

    [HttpPost("memberships")]
    public async Task<ActionResult> CreateMembership([FromBody] MembershipInput input, CancellationToken ct) =>
        Result(await _config.SaveMembershipAsync(Tenant, null, input, _actor.UserId, ct));

    [HttpPut("memberships/{id}")]
    public async Task<ActionResult> UpdateMembership(string id, [FromBody] MembershipInput input, CancellationToken ct) =>
        Result(await _config.SaveMembershipAsync(Tenant, id, input, _actor.UserId, ct));

    [HttpDelete("memberships/{id}")]
    public async Task<ActionResult> DeleteMembership(string id, CancellationToken ct) =>
        Result(await _config.DeleteMembershipAsync(Tenant, id, ct));

    /// <summary>
    /// The queues a user pulls from, in Get Next order (primary then overflow,
    /// by rank). Entitlement (the user's role) further narrows this at Get Next.
    /// </summary>
    [HttpGet("users/{userId}/queues")]
    public async Task<ActionResult<List<EffectiveQueueView>>> EffectiveQueues(string userId, CancellationToken ct) =>
        Ok((await _config.EffectiveQueuesAsync(Tenant, userId, ct)).Select(e => new EffectiveQueueView
        {
            QueueId = e.Queue.Id,
            QueueName = e.Queue.Name,
            WorkType = e.Queue.WorkType,
            Rank = e.Rank,
            IsOverflow = e.IsOverflow,
            AuthorityLimit = e.AuthorityLimit,
            Via = e.Via.ToList(),
        }).ToList());

    // ── Operations ───────────────────────────────────────────────────────

    /// <summary>Re-route and re-score every open item (after a configuration change).</summary>
    [HttpPost("recompute")]
    public async Task<ActionResult<RecomputeResult>> Recompute(CancellationToken ct) =>
        Ok(await _intake.RecomputeAsync(Tenant, reroute: true, ct));

    /// <summary>Repair drift against claims-service now (create missing items, close resolved ones).</summary>
    [HttpPost("reconcile")]
    public async Task<ActionResult<ReconcileResult>> Reconcile(CancellationToken ct)
    {
        var result = await _intake.ReconcileClaimsAsync(Tenant, _claims, Math.Clamp(_options.Reconcile.PageSize, 1, 1000), ct);
        return result.Succeeded ? Ok(result) : StatusCode(StatusCodes.Status502BadGateway, result);
    }

    /// <summary>Brings enrollment exception work in step with enrollment-import-service now.</summary>
    [HttpPost("reconcile/enrollment")]
    public async Task<ActionResult<ReconcileResult>> ReconcileEnrollment(CancellationToken ct)
    {
        var result = await _intake.ReconcileEnrollmentAsync(Tenant, _enrollment, Math.Clamp(_options.EnrollmentReconcile.PageSize, 1, 1000), ct);
        return result.Succeeded ? Ok(result) : StatusCode(StatusCodes.Status502BadGateway, result);
    }
}
