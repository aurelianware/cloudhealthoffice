using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using WorkManagementService.Models;
using WorkManagementService.Repositories;

namespace WorkManagementService.Controllers;

/// <summary>
/// The user's workspace on Home (docs/architecture/portal-navigation.md
/// §3.1): plan notices and pinned records. Reading notices, marking them read
/// and pinning need <c>workspace:use</c> (every staff role); posting, editing
/// and withdrawing notices need <c>notices:write</c> (tenant administrators).
/// The tenant and user are the token's; pins and read marks are the caller's
/// own and can't name another user.
/// </summary>
[ApiController]
[Route("api/v1/workspace")]
[Produces("application/json")]
public sealed class WorkspaceController : ControllerBase
{
    private readonly IWorkspaceStore _store;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _time;

    public WorkspaceController(IWorkspaceStore store, ICurrentActor actor, TimeProvider time)
    {
        _store = store;
        _actor = actor;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Notices: everyone ────────────────────────────────────────────────

    /// <summary>Notices shown to the caller now (window, roles), important first, newest first, with read state.</summary>
    [HttpGet("notices")]
    [RequirePermission(ChoRolePermissions.WorkspaceUse)]
    public async Task<ActionResult<IEnumerable<NoticeView>>> MyNotices(CancellationToken ct)
    {
        var now = Now;
        var notices = await _store.ListNoticesAsync(_actor.TenantId, ct);
        var read = await _store.ReadNoticeIdsAsync(_actor.TenantId, _actor.UserId, ct);
        return Ok(notices
            .Where(n => n.IsVisibleTo(_actor.Roles, now))
            .OrderByDescending(n => n.Severity).ThenByDescending(n => n.StartsAt)
            .Select(n => NoticeView.From(n, read.Contains(n.Id), now)));
    }

    [HttpPost("notices/{id}/read")]
    [RequirePermission(ChoRolePermissions.WorkspaceUse)]
    public async Task<IActionResult> MarkRead(string id, CancellationToken ct)
    {
        var notice = await _store.GetNoticeAsync(_actor.TenantId, id, ct);
        if (notice is null || !notice.IsVisibleTo(_actor.Roles, Now)) return NotFound();
        await _store.MarkReadAsync(new NoticeRead
        {
            Id = NoticeRead.KeyFor(id, _actor.UserId), TenantId = _actor.TenantId, NoticeId = id, UserId = _actor.UserId, ReadAt = Now,
        }, ct);
        return NoContent();
    }

    // ── Notices: administrators ──────────────────────────────────────────

    /// <summary>Every notice (scheduled, live, expired, withdrawn), newest first.</summary>
    [HttpGet("notices/all")]
    [RequirePermission(ChoRolePermissions.NoticesWrite)]
    public async Task<ActionResult<IEnumerable<NoticeView>>> AllNotices(CancellationToken ct)
    {
        var now = Now;
        return Ok((await _store.ListNoticesAsync(_actor.TenantId, ct))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => NoticeView.From(n, read: false, now)));
    }

    [HttpPost("notices")]
    [RequirePermission(ChoRolePermissions.NoticesWrite)]
    public async Task<ActionResult<NoticeView>> Post([FromBody] NoticeRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } problem) return BadRequest(new { error = problem });
        var notice = new PlanNotice
        {
            TenantId = _actor.TenantId,
            CreatedBy = _actor.UserId,
            CreatedByName = _actor.DisplayName,
            CreatedAt = Now,
        };
        Apply(request, notice);
        await _store.UpsertNoticeAsync(notice, ct);
        return CreatedAtAction(nameof(AllNotices), NoticeView.From(notice, read: false, Now));
    }

    [HttpPut("notices/{id}")]
    [RequirePermission(ChoRolePermissions.NoticesWrite)]
    public async Task<ActionResult<NoticeView>> Edit(string id, [FromBody] NoticeRequest request, CancellationToken ct)
    {
        var notice = await _store.GetNoticeAsync(_actor.TenantId, id, ct);
        if (notice is null) return NotFound();
        if (notice.Withdrawn) return Conflict(new { error = "A withdrawn notice can't be edited; post a new one." });
        if (Validate(request) is { } problem) return BadRequest(new { error = problem });
        Apply(request, notice);
        notice.UpdatedBy = _actor.UserId;
        notice.UpdatedAt = Now;
        await _store.UpsertNoticeAsync(notice, ct);
        return Ok(NoticeView.From(notice, read: false, Now));
    }

    [HttpPost("notices/{id}/withdraw")]
    [RequirePermission(ChoRolePermissions.NoticesWrite)]
    public async Task<ActionResult<NoticeView>> Withdraw(string id, CancellationToken ct)
    {
        var notice = await _store.GetNoticeAsync(_actor.TenantId, id, ct);
        if (notice is null) return NotFound();
        notice.Withdrawn = true;
        notice.UpdatedBy = _actor.UserId;
        notice.UpdatedAt = Now;
        await _store.UpsertNoticeAsync(notice, ct);
        return Ok(NoticeView.From(notice, read: false, Now));
    }

    private string? Validate(NoticeRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) return "A title is required.";
        if (r.Title.Length > PlanNotice.MaxTitle) return $"The title can be at most {PlanNotice.MaxTitle} characters.";
        if ((r.Body?.Length ?? 0) > PlanNotice.MaxBody) return $"The text can be at most {PlanNotice.MaxBody} characters.";
        var starts = r.StartsAt ?? Now;
        if (r.ExpiresAt is { } expires && expires <= starts) return "The notice must expire after it starts.";
        var unknown = (r.TargetRoles ?? []).Where(role => !ChoRolePermissions.BuiltInRoles.Contains(role, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) return $"Unknown role(s): {string.Join(", ", unknown)}.";
        return null;
    }

    private void Apply(NoticeRequest r, PlanNotice n)
    {
        n.Title = r.Title.Trim();
        n.Body = r.Body?.Trim() ?? string.Empty;
        n.Severity = r.Severity;
        n.StartsAt = (r.StartsAt ?? Now).ToUniversalTime();
        n.ExpiresAt = r.ExpiresAt?.ToUniversalTime();
        n.TargetRoles = (r.TargetRoles ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Pins: the caller's own ───────────────────────────────────────────

    [HttpGet("pins")]
    [RequirePermission(ChoRolePermissions.WorkspaceUse)]
    public async Task<ActionResult<IEnumerable<PinnedRecord>>> Pins(CancellationToken ct) =>
        Ok((await _store.ListPinsAsync(_actor.TenantId, _actor.UserId, ct)).OrderByDescending(p => p.PinnedAt));

    [HttpPut("pins")]
    [RequirePermission(ChoRolePermissions.WorkspaceUse)]
    public async Task<ActionResult<PinnedRecord>> Pin([FromBody] PinRequest request, CancellationToken ct)
    {
        if (!PinnedRecord.Kinds.Contains(request.Kind ?? string.Empty)) return BadRequest(new { error = "Unknown record kind." });
        if (string.IsNullOrWhiteSpace(request.RecordId)) return BadRequest(new { error = "A record id is required." });
        var kind = PinnedRecord.Kinds.First(k => string.Equals(k, request.Kind, StringComparison.OrdinalIgnoreCase));
        var id = PinnedRecord.KeyFor(_actor.UserId, kind, request.RecordId);

        var mine = await _store.ListPinsAsync(_actor.TenantId, _actor.UserId, ct);
        if (mine.All(p => p.Id != id) && mine.Count >= PinnedRecord.MaxPerUser)
            return Conflict(new { error = $"You can pin at most {PinnedRecord.MaxPerUser} records; unpin one first." });

        var pin = new PinnedRecord
        {
            Id = id, TenantId = _actor.TenantId, UserId = _actor.UserId, Kind = kind, RecordId = request.RecordId.Trim(),
            Label = string.IsNullOrWhiteSpace(request.Label) ? $"{kind} {request.RecordId}" : request.Label.Trim()[..Math.Min(request.Label.Trim().Length, 120)],
            PinnedAt = Now,
        };
        await _store.UpsertPinAsync(pin, ct);
        return Ok(pin);
    }

    [HttpDelete("pins/{kind}/{recordId}")]
    [RequirePermission(ChoRolePermissions.WorkspaceUse)]
    public async Task<IActionResult> Unpin(string kind, string recordId, CancellationToken ct)
    {
        var canonical = PinnedRecord.Kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
        if (canonical is null) return NotFound();
        return await _store.DeletePinAsync(_actor.TenantId, PinnedRecord.KeyFor(_actor.UserId, canonical, recordId), ct)
            ? NoContent() : NotFound();
    }
}

public sealed record NoticeRequest(
    string Title,
    string? Body,
    NoticeSeverity Severity = NoticeSeverity.Info,
    DateTime? StartsAt = null,
    DateTime? ExpiresAt = null,
    List<string>? TargetRoles = null);

public sealed record PinRequest(string? Kind, string? RecordId, string? Label);

/// <summary>A notice as the portal shows it.</summary>
public sealed record NoticeView(
    string Id, string Title, string Body, NoticeSeverity Severity,
    DateTime StartsAt, DateTime? ExpiresAt, IReadOnlyList<string> TargetRoles,
    string State, bool Read, string CreatedBy, string? CreatedByName, DateTime CreatedAt, DateTime? UpdatedAt)
{
    public static NoticeView From(PlanNotice n, bool read, DateTime now) => new(
        n.Id, n.Title, n.Body, n.Severity, n.StartsAt, n.ExpiresAt, n.TargetRoles,
        n.Withdrawn ? "Withdrawn" : n.StartsAt > now ? "Scheduled" : n.ExpiresAt is { } e && e <= now ? "Expired" : "Live",
        read, n.CreatedBy, n.CreatedByName, n.CreatedAt, n.UpdatedAt);
}
