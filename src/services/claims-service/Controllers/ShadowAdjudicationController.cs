using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Shadow adjudication: re-adjudicates claims the plan's core system already
/// adjudicated (loaded through the CPCDS file drop) in CHO's own pipeline and
/// reports where the answers differ. Nothing about the claim changes: no
/// status, no EOB, no accumulator, no event. See
/// <c>docs/integration/SHADOW-ADJUDICATION.md</c>.
/// </summary>
[ApiController]
[Route("api/v1/claims/shadow-adjudication")]
[Produces("application/json")]
public class ShadowAdjudicationController : ControllerBase
{
    private const int MaxListLimit = 1000;

    private readonly IShadowAdjudicationService _service;
    private readonly IShadowAdjudicationRepository _store;
    private readonly ICurrentActor _actor;

    public ShadowAdjudicationController(
        IShadowAdjudicationService service, IShadowAdjudicationRepository store, ICurrentActor actor)
    {
        _service = service;
        _store = store;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Runs a shadow adjudication over the named claims, or the claims of one
    /// CPCDS import batch, and returns the run's totals. Per-claim results:
    /// <c>GET runs/{runId}/results</c>.
    /// </summary>
    [HttpPost("runs")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowAdjudicationRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShadowAdjudicationRun>> Run(
        [FromBody] ShadowAdjudicationRunRequest? request, CancellationToken ct)
    {
        if (request is null || ((request.ClaimIds is null || request.ClaimIds.Count == 0)
                                && string.IsNullOrWhiteSpace(request.ImportBatchId)))
        {
            return BadRequest(new { error = "Name the claims (claimIds) or a CPCDS import batch (importBatchId)." });
        }
        if (request.Limit is < 1)
            return BadRequest(new { error = "limit must be 1 or more." });

        try
        {
            var response = await _service.RunAsync(TenantId, request, _actor.UserId, ct);
            return Ok(response.Run);
        }
        catch (ShadowAdjudicationDisabledException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>The tenant's recent shadow runs, newest first.</summary>
    [HttpGet("runs")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<IReadOnlyList<ShadowAdjudicationRun>>> ListRuns(
        [FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await _store.ListRunsAsync(TenantId, Math.Clamp(limit, 1, MaxListLimit), ct));

    [HttpGet("runs/{runId}")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<ShadowAdjudicationRun>> GetRun(string runId, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(TenantId, runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>
    /// A run's per-claim comparisons, largest paid difference first. Filter
    /// with <c>kind</c> (Match, ChoUndecided, OutcomeMismatch, DenialCodeMismatch,
    /// AllowedVariance, PaidVariance, MemberLiabilityVariance, NotComparable).
    /// </summary>
    [HttpGet("runs/{runId}/results")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<IReadOnlyList<ShadowAdjudicationResult>>> ListResults(
        string runId, [FromQuery] string? kind = null, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (kind is not null && !ShadowDiscrepancyKind.All.Contains(kind, StringComparer.Ordinal))
            return BadRequest(new { error = $"kind must be one of: {string.Join(", ", ShadowDiscrepancyKind.All)}." });
        if (await _store.GetRunAsync(TenantId, runId, ct) is null)
            return NotFound();
        return Ok(await _store.ListResultsAsync(TenantId, runId, kind, Math.Clamp(limit, 1, MaxListLimit), ct));
    }
}
