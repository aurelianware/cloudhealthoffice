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

    /// <summary>
    /// The claims the caller opened recently, newest first, one row per
    /// claim: the "Recent" list on Home. Built from the caller's own access
    /// events, so it needs only claims:read and never shows anyone else's.
    /// </summary>
    [HttpGet("recently-viewed")]
    [AuditClaimAccess(ClaimAccessActions.List)]
    [ProducesResponseType(typeof(IEnumerable<RecentClaimView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RecentClaimView>>> RecentlyViewed(
        [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var events = await _audit.QueryAsync(new ClaimAccessQuery(TenantId, ActorId: _actor.UserId, Limit: 300), ct);
        return Ok(RecentClaimView.From(events, limit));
    }
}

/// <summary>A claim the caller opened, for the Home "Recent" list.</summary>
public sealed record RecentClaimView(string ClaimId, string? ClaimNumber, string? MemberId, DateTime ViewedAt)
{
    /// <summary>Allowed single-claim views, newest first, one per claim.</summary>
    public static IReadOnlyList<RecentClaimView> From(IEnumerable<ClaimAccessEvent> events, int limit) =>
        events
            .Where(e => e.Action == ClaimAccessActions.View && e.Outcome == ClaimAccessOutcomes.Allowed && !string.IsNullOrWhiteSpace(e.ClaimId))
            .OrderByDescending(e => e.OccurredAt)
            .GroupBy(e => e.ClaimId!)
            .Select(g => g.First())
            .Take(limit)
            .Select(e => new RecentClaimView(e.ClaimId!, e.ClaimNumber, e.MemberId, e.OccurredAt))
            .ToList();
}
