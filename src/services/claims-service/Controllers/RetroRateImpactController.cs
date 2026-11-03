using System.Globalization;
using System.Text;
using ClaimsService.Services.RetroRate;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Retro rate impact: what a fee schedule change would do to claims already
/// paid on the old rates. A dry run: claims, payments, EOBs and accumulators
/// are untouched. See docs/integration/RETRO-RATE-REPROCESSING.md.
/// </summary>
[ApiController]
[Route("api/v1/claims/retro-rate")]
[Produces("application/json")]
public class RetroRateImpactController : ControllerBase
{
    private const int MaxListLimit = 1000;
    internal const int MaxCsvResults = 50_000;

    private readonly IRetroRateImpactService _service;
    private readonly IRetroRateImpactRepository _store;
    private readonly ICurrentActor _actor;

    public RetroRateImpactController(IRetroRateImpactService service, IRetroRateImpactRepository store, ICurrentActor actor)
    {
        _service = service;
        _store = store;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Measures one fee schedule change (<c>feeScheduleId</c>, optionally the
    /// <c>revision</c> it stored; the latest change otherwise) against the
    /// paid claims it touches. Returns the run's totals; per-claim results are
    /// at <c>impact-runs/{id}/results</c> and <c>impact-runs/{id}/claims.csv</c>.
    /// </summary>
    [HttpPost("impact-runs")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [ProducesResponseType(typeof(RetroRateImpactRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RetroRateImpactRun>> Run([FromBody] RetroRateImpactRunRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FeeScheduleId))
            return BadRequest(new { error = "feeScheduleId is required." });
        if (request.Limit is < 1)
            return BadRequest(new { error = "limit must be 1 or more." });
        if (request.Revision is < 1)
            return BadRequest(new { error = "revision must be 1 or more." });

        try
        {
            return Ok(await _service.RunAsync(TenantId, request, _actor.UserId, ct));
        }
        catch (FeeScheduleChangeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>The tenant's recent impact runs, newest first.</summary>
    [HttpGet("impact-runs")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    public async Task<ActionResult<IReadOnlyList<RetroRateImpactRun>>> ListRuns([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await _store.ListRunsAsync(TenantId, Math.Clamp(limit, 1, MaxListLimit), ct));

    [HttpGet("impact-runs/{runId}")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    public async Task<ActionResult<RetroRateImpactRun>> GetRun(string runId, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(TenantId, runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>
    /// A run's per-claim results, largest paid difference first. Filter with
    /// <c>direction</c> (Underpaid, Overpaid, Unchanged, Undecided).
    /// </summary>
    [HttpGet("impact-runs/{runId}/results")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    public async Task<ActionResult<IReadOnlyList<RetroRateImpactResult>>> ListResults(
        string runId, [FromQuery] string? direction = null, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (direction is not null && !RetroRateDirection.All.Contains(direction, StringComparer.Ordinal))
            return BadRequest(new { error = $"direction must be one of: {string.Join(", ", RetroRateDirection.All)}." });
        if (await _store.GetRunAsync(TenantId, runId, ct) is null)
            return NotFound();
        return Ok(await _store.ListResultsAsync(TenantId, runId, direction, Math.Clamp(limit, 1, MaxListLimit), ct));
    }

    /// <summary>The run's claims as CSV, one row per claim, largest paid difference first.</summary>
    [HttpGet("impact-runs/{runId}/claims.csv")]
    [RequirePermission(ClaimsPermissions.Adjust)]
    [Produces("text/csv")]
    public async Task<IActionResult> ClaimsCsv(string runId, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(TenantId, runId, ct);
        if (run is null) return NotFound();
        var results = await _store.ListResultsAsync(TenantId, runId, direction: null, MaxCsvResults, ct);
        return File(Encoding.UTF8.GetBytes(WriteCsv(results)), "text/csv; charset=utf-8", $"retro-rate-impact-{run.Id}.csv");
    }

    internal static string WriteCsv(IEnumerable<RetroRateImpactResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("claim_number,claim_id,status,service_date,billing_npi,plan,match,direction,affected_lines,priced_on_revision,paid_allowed,repriced_allowed,paid,repriced,net_paid_delta,member_liability_delta,pend_code,pend_reason");
        foreach (var r in results)
        {
            sb.AppendJoin(',',
                Cell(r.ClaimNumber), Cell(r.ClaimId), Cell(r.ClaimStatus),
                r.ServiceDateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Cell(r.BillingProviderNpi), Cell(r.BenefitPlanId), Cell(r.Match), Cell(r.Direction),
                Cell(string.Join(' ', r.AffectedLines)),
                r.PricedOnRevision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Money(r.PaidAllowed), Money(r.RepricedAllowed), Money(r.Paid), Money(r.Repriced),
                Money(r.NetPaidDelta), Money(r.MemberLiabilityDelta),
                Cell(r.PendCode), Cell(r.PendReason));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting, and a leading quote on cells a spreadsheet would run as a formula.</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
