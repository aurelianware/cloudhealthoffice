using System.Text;
using ClaimsService.Models.Tpl;
using ClaimsService.Services.Tpl;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Medicaid third-party liability recoveries: cases for money Medicaid paid
/// that another payer owed. See docs/integration/MEDICAID-TPL.md.
/// Reads need <c>claims:read</c>; changes need <c>claims:work</c>.
/// </summary>
[ApiController]
[Route("api/v1/claims/tpl-recoveries")]
[Produces("application/json")]
public sealed class TplRecoveriesController : ControllerBase
{
    private const int MaxSyncLimit = 5000;

    private readonly ITplRecoveryService _service;
    private readonly ITplRecoveryRepository _cases;
    private readonly ICurrentActor _actor;

    public TplRecoveriesController(ITplRecoveryService service, ITplRecoveryRepository cases, ICurrentActor actor)
    {
        _service = service;
        _cases = cases;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Opens a case for every Medicaid claim adjudicated in [from, to) whose
    /// pay-and-chase lines were paid. Idempotent: a claim already holding a
    /// case is counted, not duplicated. Run daily, or after a payment cycle.
    /// </summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(TplRecoverySyncResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TplRecoverySyncResult>> Sync(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        var end = to ?? DateTime.UtcNow.Date.AddDays(1);
        var start = from ?? end.AddDays(-30);
        if (start >= end) return BadRequest(new { error = "from must be before to." });
        return Ok(await _service.SyncPayAndChaseAsync(TenantId, start, end, Math.Clamp(limit, 1, MaxSyncLimit), _actor.UserId, ct));
    }

    /// <summary>
    /// Checks the members' paid Medicaid claims (within the lookback) against
    /// their TPL now and opens a case for lines other coverage turns out to
    /// have covered. Call it with the members a TPL file load added coverage for.
    /// </summary>
    [HttpPost("retroactive")]
    [ProducesResponseType(typeof(TplRetroactiveResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TplRetroactiveResult>> Retroactive(
        [FromBody] TplRetroactiveRequest? request, CancellationToken ct = default)
    {
        if (request?.MemberIds is not { Count: > 0 } members)
            return BadRequest(new { error = "Name the members (memberIds)." });
        if (members.Count > 1000)
            return BadRequest(new { error = "At most 1,000 members per call." });
        return Ok(await _service.CheckRetroactiveAsync(TenantId, members, request.AsOf ?? DateTime.UtcNow, _actor.UserId, ct));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TplRecoveryCase>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TplRecoveryCase>>> List(
        [FromQuery] string? state = null,
        [FromQuery] TplRecoveryStatus? status = null,
        [FromQuery] bool openOnly = false,
        [FromQuery] DateTime? deadlineBefore = null,
        [FromQuery] string? memberId = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default) =>
        Ok(await _cases.ListAsync(TenantId,
            new TplRecoveryQuery(state, status, openOnly, deadlineBefore, null, null, memberId, Math.Clamp(limit, 1, 1000)), ct));

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TplRecoveryCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TplRecoveryCase>> Get(string id, CancellationToken ct = default) =>
        await _cases.GetAsync(TenantId, id, ct) is { } c ? Ok(c) : NotFound();

    /// <summary>Records a bill sent, a payment received, a repayment plan, a carrier denial, a write-off or a note.</summary>
    [HttpPost("{id}/events")]
    [ProducesResponseType(typeof(TplRecoveryCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TplRecoveryCase>> AddEvent(
        string id, [FromBody] TplRecoveryEventRequest? request, CancellationToken ct = default)
    {
        if (request is null) return BadRequest(new { error = "Body required." });
        if (request.Type is TplRecoveryEventType.Opened or TplRecoveryEventType.TransferredToState)
            return BadRequest(new { error = $"{request.Type} is recorded by the system." });
        try
        {
            var updated = await _service.RecordEventAsync(TenantId, id, request, _actor.UserId, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Hands cases whose state recovery window has closed (California: 12
    /// months after payment) to the state, unless a repayment plan was agreed
    /// in time. Run daily.
    /// </summary>
    [HttpPost("apply-clocks")]
    [ProducesResponseType(typeof(TplClockResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TplClockResult>> ApplyClocks([FromQuery] DateTime? asOf = null, CancellationToken ct = default) =>
        Ok(await _service.ApplyClocksAsync(TenantId, asOf ?? DateTime.UtcNow, _actor.UserId, ct));

    /// <summary>The month's state report as CSV (cases handed to the state, recoveries received).</summary>
    [HttpGet("report")]
    [Produces("text/csv")]
    public async Task<IActionResult> Report([FromQuery] string state, [FromQuery] DateTime month, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Trim().Length != 2)
            return BadRequest(new { error = "state (two letters) is required." });
        var csv = await _service.BuildStateReportCsvAsync(TenantId, state.Trim().ToUpperInvariant(), month, ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"tpl-recoveries-{state.ToUpperInvariant()}-{month:yyyy-MM}.csv");
    }
}

public sealed record TplRetroactiveRequest(IReadOnlyList<string>? MemberIds, DateTime? AsOf);
