using System.Text;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The recovery worklist: one finding per claim where a shadow run says the
/// core paid the wrong amount, the plan's review of each (approved, rejected,
/// in recovery, recovered, appealed), provider roll-ups, the recoupment
/// lookback, and a CSV for the plan's own mass adjustment. CHO never writes to
/// the core and never contacts a provider. Reading needs <c>claims:import</c>
/// (as the Shadow Adjudication page does); changing a status or the lookback
/// needs <c>claims:adjust</c>. See docs/integration/SHADOW-ADJUDICATION.md#recovery-worklist.
/// </summary>
[ApiController]
[Route("api/v1/claims/shadow-adjudication")]
[Produces("application/json")]
public class ShadowFindingsController : ControllerBase
{
    private const int MaxList = 1000;

    /// <summary>Most findings a summary or export reads.</summary>
    internal const int MaxRead = 50_000;

    private readonly IShadowFindingRepository _findings;
    private readonly IShadowFindingService _service;
    private readonly IShadowAdjudicationRepository _runs;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public ShadowFindingsController(
        IShadowFindingRepository findings, IShadowFindingService service, IShadowAdjudicationRepository runs,
        ICurrentActor actor, TimeProvider? clock = null)
    {
        _findings = findings;
        _service = service;
        _runs = runs;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    private DateTime Today => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Findings, largest amount first. Filter by <c>status</c>, <c>direction</c>
    /// (CorePaidMore, CorePaidLess) or <c>billingProviderNpi</c>; <c>lookback</c>
    /// (Past, DueSoon, Within, Unknown) filters on today's lookback state.
    /// </summary>
    [HttpGet("findings")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(IReadOnlyList<ShadowFindingView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ShadowFindingView>>> List(
        [FromQuery] string? status = null, [FromQuery] string? direction = null, [FromQuery] string? billingProviderNpi = null,
        [FromQuery] string? lookback = null, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var parsed = status is null ? null : ShadowFindingStatus.Parse(status);
        if (status is not null && parsed is null)
            return BadRequest(new { error = $"status must be one of: {string.Join(", ", ShadowFindingStatus.All)}." });

        var views = await ViewsAsync(parsed, direction, billingProviderNpi,
            lookback is null ? Math.Clamp(limit, 1, MaxList) : MaxRead, ct);
        if (lookback is not null)
            views = views.Where(v => string.Equals(v.LookbackState, lookback, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(limit, 1, MaxList)).ToList();
        return Ok(views);
    }

    [HttpGet("findings/{id}")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<ShadowFindingView>> Get(string id, CancellationToken ct = default)
    {
        var finding = await _findings.GetAsync(TenantId, id, ct);
        if (finding is null) return NotFound();
        var lookback = await _findings.GetLookbackAsync(TenantId, ct);
        return Ok(ShadowFindingView.For(finding, lookback, Today));
    }

    /// <summary>Totals by status, recovered against confirmed, the lookback, and billing providers.</summary>
    [HttpGet("findings/summary")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<ShadowFindingSummary>> Summary(
        [FromQuery] int topProviders = 25, CancellationToken ct = default)
    {
        var views = await ViewsAsync(null, null, null, MaxRead, ct);
        return Ok(ShadowFindingSummaryBuilder.Build(views, topProviders));
    }

    /// <summary>
    /// Records the plan's review: <c>status</c> (Approved, Rejected, InRecovery,
    /// Recovered, Appealed, or New to reopen a rejected one), an optional
    /// <c>note</c>, and <c>recoveredAmount</c> for Recovered. A change that
    /// races another reviewer's is refused with 409.
    /// </summary>
    [HttpPost("findings/{id}/status")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(ShadowFindingView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShadowFindingView>> SetStatus(
        string id, [FromBody] ShadowFindingStatusRequest request, CancellationToken ct = default)
    {
        var finding = await _findings.GetAsync(TenantId, id, ct);
        if (finding is null) return NotFound();

        var was = finding.Status;
        var error = ShadowFindingBuilder.Move(finding, request, _actor.UserId, Today);
        if (error is not null) return BadRequest(new { error });
        if (!await _findings.ReplaceIfStatusAsync(finding, was, ct))
            return Conflict(new { error = "Someone else changed this finding. Reload it and try again." });

        var lookback = await _findings.GetLookbackAsync(TenantId, ct);
        return Ok(ShadowFindingView.For(finding, lookback, Today));
    }

    /// <summary>
    /// The worklist as CSV for the plan's own mass adjustment. Default: the
    /// Approved findings; <c>status</c> may be repeated (Approved, InRecovery, ...).
    /// </summary>
    [HttpGet("findings/export.csv")]
    [RequirePermission(ClaimsPermissions.Import)]
    [Produces("text/csv")]
    public async Task<IActionResult> Export([FromQuery] string[]? status = null, CancellationToken ct = default)
    {
        var statuses = (status is { Length: > 0 } ? status : [ShadowFindingStatus.Approved])
            .Select(ShadowFindingStatus.Parse).ToList();
        if (statuses.Any(s => s is null))
            return BadRequest(new { error = $"status must be one of: {string.Join(", ", ShadowFindingStatus.All)}." });

        var views = new List<ShadowFindingView>();
        foreach (var s in statuses.Distinct())
            views.AddRange(await ViewsAsync(s, null, null, MaxRead, ct));
        var csv = ShadowFindingCsv.Write(views.OrderByDescending(v => v.Finding.Amount));
        return File(Encoding.UTF8.GetBytes(csv), "text/csv",
            $"shadow-findings-{string.Join("-", statuses.Distinct()).ToLowerInvariant()}-{Today:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Adds a finished run's payment differences to the worklist (done
    /// automatically after each run unless Adjudication:Shadow:AddFindingsAfterRun
    /// is off). Safe to repeat: findings are keyed by claim.
    /// </summary>
    [HttpPost("runs/{runId}/findings")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<IActionResult> AddRun(string runId, CancellationToken ct = default)
    {
        var run = await _runs.GetRunAsync(TenantId, runId, ct);
        if (run is null) return NotFound();
        if (run.Status != ShadowRunStatus.Completed)
            return Conflict(new { error = $"The run is {run.Status}; findings are added once it completes." });
        var results = await _runs.ListResultsAsync(TenantId, runId, kind: null, ShadowAdjudicationController.MaxReportResults, ct);
        var n = await _service.AddRunAsync(run, results, ct);
        return Ok(new { runId, findingsAddedOrUpdated = n });
    }

    /// <summary>The plan's recoupment lookback: a default in days and per-provider overrides.</summary>
    [HttpGet("findings/lookback")]
    [RequirePermission(ClaimsPermissions.Import)]
    public async Task<ActionResult<ShadowRecoveryLookbackSettings>> GetLookback(CancellationToken ct = default) =>
        Ok(await _findings.GetLookbackAsync(TenantId, ct) ?? new ShadowRecoveryLookbackSettings { Id = TenantId });

    [HttpPut("findings/lookback")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(ShadowRecoveryLookbackSettings), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ShadowRecoveryLookbackSettings>> PutLookback(
        [FromBody] ShadowRecoveryLookbackSettings settings, CancellationToken ct = default)
    {
        settings.Id = TenantId;
        settings.Providers = settings.Providers
            .Select(p => new ShadowProviderLookback { Npi = p.Npi?.Trim() ?? string.Empty, ContractNumber = p.ContractNumber?.Trim(), Days = p.Days })
            .ToList();
        var errors = settings.Validate();
        if (errors.Count > 0) return BadRequest(new { error = "invalid_lookback", errors });
        settings.UpdatedAtUtc = Today;
        settings.UpdatedBy = _actor.UserId;
        await _findings.SaveLookbackAsync(settings, ct);
        return Ok(settings);
    }

    private async Task<List<ShadowFindingView>> ViewsAsync(
        string? status, string? direction, string? npi, int limit, CancellationToken ct)
    {
        var findings = await _findings.ListAsync(TenantId, status, direction, npi, limit, ct);
        var lookback = await _findings.GetLookbackAsync(TenantId, ct);
        var today = Today;
        return findings.Select(f => ShadowFindingView.For(f, lookback, today)).ToList();
    }
}
