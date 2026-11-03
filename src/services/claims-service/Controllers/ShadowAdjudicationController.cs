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

    /// <summary>Most results one report or CSV reads (a run holds at most MaxClaimsPerRun).</summary>
    internal const int MaxReportResults = 50_000;

    private readonly IShadowAdjudicationService _service;
    private readonly IShadowAdjudicationRepository _store;
    private readonly ShadowAdjudicationTenants _tenants;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public ShadowAdjudicationController(
        IShadowAdjudicationService service, IShadowAdjudicationRepository store, ShadowAdjudicationTenants tenants,
        ICurrentActor actor, TimeProvider? clock = null)
    {
        _service = service;
        _store = store;
        _tenants = tenants;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Whether shadow adjudication runs for this tenant: the deployment's switch,
    /// the tenant's own choice, the run size cap and the tolerance.
    /// </summary>
    [HttpGet("settings")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowAdjudicationSettingsView), StatusCodes.Status200OK)]
    public async Task<ActionResult<ShadowAdjudicationSettingsView>> GetSettings(CancellationToken ct = default)
        => Ok(await _tenants.DescribeAsync(TenantId, ct));

    /// <summary>
    /// Turns shadow adjudication on or off for this tenant, without a
    /// redeploy. Replaces what deployment configuration says for the tenant.
    /// Nothing runs while the deployment's switch is off.
    /// </summary>
    [HttpPut("settings")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowAdjudicationSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ShadowAdjudicationSettingsView>> PutSettings(
        [FromBody] ShadowAdjudicationSettingsRequest? request, CancellationToken ct = default)
    {
        if (request is null)
            return BadRequest(new { error = "Send { \"enabled\": true } or { \"enabled\": false }." });
        await _tenants.SaveAsync(TenantId, request.Enabled, _actor.UserId, _clock.GetUtcNow().UtcDateTime, ct);
        return Ok(await _tenants.DescribeAsync(TenantId, ct));
    }

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

    /// <summary>
    /// The run's report: how much CHO decided, how often it agreed with the
    /// core, money the core paid more or less than CHO would have, and the top
    /// reasons (gap, CHO stage, denial codes, procedure, billing provider).
    /// </summary>
    [HttpGet("runs/{runId}/report")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowRunReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShadowRunReport>> Report(
        string runId, [FromQuery] int top = ShadowRunReportBuilder.DefaultTopN, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(TenantId, runId, ct);
        if (run is null) return NotFound();
        var results = await _store.ListResultsAsync(TenantId, runId, kind: null, MaxReportResults, ct);
        return Ok(ShadowRunReportBuilder.Build(run, results, top));
    }

    /// <summary>
    /// The run's discrepancies as CSV: one row per differing line (or per
    /// claim when no single line differs). Set <c>includeUndecided</c> to add
    /// the claims CHO could not decide, with the missing input.
    /// </summary>
    [HttpGet("runs/{runId}/discrepancies.csv")]
    [RequirePermission(ClaimsPermissions.Import)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiscrepanciesCsv(
        string runId, [FromQuery] bool includeUndecided = false, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(TenantId, runId, ct);
        if (run is null) return NotFound();
        var results = await _store.ListResultsAsync(TenantId, runId, kind: null, MaxReportResults, ct);
        var csv = ShadowDiscrepancyCsv.Write(results, includeUndecided);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
            $"shadow-adjudication-{run.Id}.csv");
    }
}
