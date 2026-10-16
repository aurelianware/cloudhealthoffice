using ClaimsService.Audit;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The claim access log: who read which claims, when, through which
/// endpoint, and whether it was allowed. Needs <c>audit:read</c>
/// (compliance roles). Reading the log is itself recorded.
/// </summary>
[ApiController]
[Route("api/claims")]
[Produces("application/json")]
public class ClaimAccessLogController : ControllerBase
{
    private readonly IClaimAccessAuditRepository _audit;
    private readonly ICurrentActor _actor;

    public ClaimAccessLogController(IClaimAccessAuditRepository audit, ICurrentActor actor)
    {
        _audit = audit;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>Everyone who read this claim, newest first: single-claim reads and searches that returned it.</summary>
    [HttpGet("{claimId}/access-log")]
    [RequirePermission(ClaimsPermissions.AuditRead)]
    [AuditClaimAccess(ClaimAccessActions.AccessLog, ClaimIdRouteValue = "claimId")]
    [ProducesResponseType(typeof(IEnumerable<ClaimAccessEvent>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ClaimAccessEvent>>> ForClaim(
        string claimId, [FromQuery] int limit = 200, CancellationToken ct = default) =>
        Ok(await _audit.QueryAsync(new ClaimAccessQuery(TenantId, ClaimId: claimId, Limit: limit), ct));

    /// <summary>
    /// The access log by member, by user, and/or by period: "who looked at
    /// this member's claims", "what did this user look at".
    /// </summary>
    [HttpGet("access-log")]
    [RequirePermission(ClaimsPermissions.AuditRead)]
    [AuditClaimAccess(ClaimAccessActions.AccessLog, MemberQueryParameters = ["memberId"])]
    [ProducesResponseType(typeof(IEnumerable<ClaimAccessEvent>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<ClaimAccessEvent>>> Search(
        [FromQuery] string? memberId, [FromQuery] string? actorId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId) && string.IsNullOrWhiteSpace(actorId) && from is null)
            return BadRequest("Give a memberId, an actorId, or a from date.");
        if (from is not null && to is not null && from > to)
            return BadRequest("from must be before to.");
        return Ok(await _audit.QueryAsync(new ClaimAccessQuery(TenantId, MemberId: memberId, ActorId: actorId, From: from, To: to, Limit: limit), ct));
    }
}
