using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Work;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// 834 changes CHO could not apply, and what the enrollment specialist does
/// about them (docs/architecture/enrollment-operations.md §3). Reading needs
/// enrollment:read; every action needs enrollment:process. Tenant and actor
/// come from the token.
/// </summary>
[ApiController]
[Route("api/v1/enrollment/exceptions")]
public sealed class ExceptionsController : ControllerBase
{
    private readonly IEnrollmentExceptionRepository _exceptions;
    private readonly EnrollmentExceptionService _service;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public ExceptionsController(IEnrollmentExceptionRepository exceptions, EnrollmentExceptionService service, ICurrentActor actor, TimeProvider clock)
    {
        _exceptions = exceptions;
        _service = service;
        _actor = actor;
        _clock = clock;
    }

    [HttpGet]
    [RequirePermission("enrollment:read")]
    public async Task<IActionResult> List([FromQuery] EnrollmentExceptionStatus? status, [FromQuery] string? reasonCode,
        [FromQuery] string? groupNumber, [FromQuery] string? memberId, [FromQuery] string? ediFileId,
        [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(await _exceptions.ListAsync(_actor.TenantId, new EnrollmentExceptionQuery
        {
            Status = status,
            ReasonCode = reasonCode?.Trim(),
            GroupNumber = groupNumber?.Trim(),
            MemberId = memberId?.Trim(),
            EdiFileId = ediFileId?.Trim(),
            Limit = limit,
        }, ct));

    /// <summary>
    /// Exceptions that need a specialist now, for work-management's reconcile:
    /// open ones and holds that have run out, oldest first, without names or
    /// demographics.
    /// </summary>
    [HttpGet("work-snapshots")]
    [RequirePermission("enrollment:read")]
    public async Task<IActionResult> WorkSnapshots([FromQuery] int page = 1, [FromQuery] int pageSize = 500, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var (items, total) = await _exceptions.ListWorkableAsync(_actor.TenantId, now, Math.Max(1, page), pageSize, ct);
        return Ok(new EnrollmentWorkSnapshotPage
        {
            Page = Math.Max(1, page),
            PageSize = pageSize,
            TotalCount = total,
            ObservedAt = now,
            Items = items.Select(e => new EnrollmentWorkSnapshot
            {
                ExceptionId = e.Id,
                Number = e.Number,
                ReasonCode = e.ReasonCode,
                GroupNumber = e.GroupNumber,
                MemberId = e.MemberId,
                MaintenanceTypeCode = e.MaintenanceTypeCode,
                CreatedAt = e.CreatedAt,
                // A hold that has run out counts as a change, so the item reopens.
                SourceVersion = Math.Max(e.UpdatedAt.Ticks, e.HeldUntil?.Ticks ?? 0),
            }).ToList(),
        });
    }

    [HttpGet("{id}")]
    [RequirePermission("enrollment:read")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await _exceptions.GetAsync(_actor.TenantId, id, ct) is { } e ? Ok(e) : NotFound();

    [HttpPost("{id}/reprocess")]
    [RequirePermission("enrollment:process")]
    public async Task<IActionResult> Reprocess(string id, [FromBody] ReprocessExceptionRequest? request, CancellationToken ct) =>
        ToResult(await _service.ReprocessAsync(_actor.TenantId, id, _actor.UserId, request?.Subscriber, request?.Dependent, request?.Note, ct));

    [HttpPost("{id}/match")]
    [RequirePermission("enrollment:process")]
    public async Task<IActionResult> Match(string id, [FromBody] MatchExceptionRequest request, CancellationToken ct) =>
        ToResult(await _service.MatchAsync(_actor.TenantId, id, _actor.UserId, request.MemberId, request.Note, ct));

    [HttpPost("{id}/hold")]
    [RequirePermission("enrollment:process")]
    public async Task<IActionResult> Hold(string id, [FromBody] HoldExceptionRequest request, CancellationToken ct) =>
        ToResult(await _service.HoldAsync(_actor.TenantId, id, _actor.UserId, request.Until.ToUniversalTime(), request.Note, ct));

    [HttpPost("{id}/send-back")]
    [RequirePermission("enrollment:process")]
    public async Task<IActionResult> SendBack(string id, [FromBody] ExceptionNoteRequest request, CancellationToken ct) =>
        ToResult(await _service.SendBackAsync(_actor.TenantId, id, _actor.UserId, request.Note, ct));

    [HttpPost("{id}/no-action")]
    [RequirePermission("enrollment:process")]
    public async Task<IActionResult> NoAction(string id, [FromBody] ExceptionNoteRequest request, CancellationToken ct) =>
        ToResult(await _service.NoActionAsync(_actor.TenantId, id, _actor.UserId, request.Note, ct));

    private IActionResult ToResult(ExceptionActionResult result) => result.Outcome switch
    {
        ExceptionActionOutcome.Done => Ok(result.Exception),
        ExceptionActionOutcome.NotFound => NotFound(),
        ExceptionActionOutcome.Conflict => Conflict(new { error = result.Error }),
        _ => UnprocessableEntity(new { error = result.Error }),
    };
}

public sealed class ReprocessExceptionRequest
{
    /// <summary>A corrected subscriber loop (subscriber exceptions only); omit to reprocess as received.</summary>
    public MemberEnrollment? Subscriber { get; set; }

    /// <summary>A corrected dependent (dependent exceptions only).</summary>
    public Dependent? Dependent { get; set; }
    public string? Note { get; set; }
}

public sealed class MatchExceptionRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class HoldExceptionRequest
{
    public DateTime Until { get; set; }
    public string? Note { get; set; }
}

public sealed class ExceptionNoteRequest
{
    public string Note { get; set; } = string.Empty;
}
