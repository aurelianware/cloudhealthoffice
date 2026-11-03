using System.Diagnostics;
using ClaimsService.Services.RetroRate;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Retro rate reprocessing runs: turn an impact run into adjustments, with a
/// second person's approval. Underpayments are paid; overpayments are recouped
/// only on plans that opt in (<c>Adjudication:RetroRate:Tenants:{tenant}</c>).
/// See docs/integration/RETRO-RATE-REPROCESSING.md.
/// </summary>
[ApiController]
[Route("api/v1/claims/retro-rate/reprocessing-runs")]
[Produces("application/json")]
public class RetroRateReprocessingController : ControllerBase
{
    private readonly IRetroRateReprocessingService _service;
    private readonly IRetroRateReprocessingRepository _store;
    private readonly ICurrentActor _actor;

    public RetroRateReprocessingController(
        IRetroRateReprocessingService service, IRetroRateReprocessingRepository store, ICurrentActor actor)
    {
        _service = service;
        _store = store;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Plans the adjustments an impact run calls for (all its claims, or
    /// <c>claimIds</c>). Nothing changes until another person approves it and
    /// it is executed.
    /// </summary>
    [HttpPost]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(RetroRateReprocessingRun), StatusCodes.Status200OK)]
    public Task<ActionResult<RetroRateReprocessingRun>> Propose([FromBody] RetroRateReprocessingRunRequest? request, CancellationToken ct) =>
        Run(() => _service.ProposeAsync(TenantId, request ?? new RetroRateReprocessingRunRequest(), _actor.UserId, ct));

    [HttpPost("{runId}/approve")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(RetroRateReprocessingRun), StatusCodes.Status200OK)]
    public Task<ActionResult<RetroRateReprocessingRun>> Approve(string runId, CancellationToken ct) =>
        Run(() => _service.ApproveAsync(TenantId, runId, _actor.UserId, ct));

    /// <summary>
    /// Creates the approved run's adjustments. Each one re-adjudicates its
    /// claim at the current rates; the reversal run then reverses the original
    /// and the corrected claim pays (or recoups) the difference on the 835.
    /// </summary>
    [HttpPost("{runId}/execute")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(RetroRateReprocessingRun), StatusCodes.Status200OK)]
    public Task<ActionResult<RetroRateReprocessingRun>> Execute(string runId, CancellationToken ct) =>
        Run(() => _service.ExecuteAsync(TenantId, runId, _actor.UserId, CorrelationId(), ct));

    [HttpPost("{runId}/cancel")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(RetroRateReprocessingRun), StatusCodes.Status200OK)]
    public Task<ActionResult<RetroRateReprocessingRun>> Cancel(string runId, CancellationToken ct) =>
        Run(() => _service.CancelAsync(TenantId, runId, _actor.UserId, ct));

    [HttpGet]
    [RequirePermission(ClaimsPermissions.Adjust)]
    public async Task<ActionResult<IReadOnlyList<RetroRateReprocessingRun>>> List([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await _store.ListAsync(TenantId, Math.Clamp(limit, 1, 500), ct));

    [HttpGet("{runId}")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    public async Task<ActionResult<RetroRateReprocessingRun>> Get(string runId, CancellationToken ct)
    {
        var run = await _store.GetAsync(TenantId, runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    private async Task<ActionResult<RetroRateReprocessingRun>> Run(Func<Task<RetroRateReprocessingRun>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (RetroRateReprocessingException ex)
        {
            return ex.Reason switch
            {
                RetroRateReprocessingException.Kind.NotFound => NotFound(new { error = ex.Message }),
                RetroRateReprocessingException.Kind.Invalid => BadRequest(new { error = ex.Message }),
                _ => Conflict(new { error = ex.Message }),
            };
        }
    }

    private string? CorrelationId() =>
        HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var header) && !string.IsNullOrEmpty(header.ToString())
            ? header.ToString()
            : Activity.Current?.Id;
}
