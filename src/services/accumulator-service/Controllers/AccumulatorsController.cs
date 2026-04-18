using AccumulatorService.Models;
using AccumulatorService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AccumulatorService.Controllers;

/// <summary>
/// HTTP surface for accumulator queries and manual adjustments.
///
/// Two route roots are exposed:
///   - /api/v1/accumulators/{memberId}                  — canonical per spec
///   - /api/v1/members/{memberId}/accumulators          — compat alias for member-service
///
/// TODO(deprecate-members-accumulators-alias): the /members/{memberId}/accumulators
/// alias is a transitional shape so member-service's existing HttpAccumulatorServiceClient
/// keeps working. Track deprecation alongside the /api/v1/plans pattern retired in PR #652.
/// </summary>
[ApiController]
public class AccumulatorsController : ControllerBase
{
    public string TenantId { get; set; } = string.Empty;

    private readonly IAccumulatorService _svc;
    private readonly ILogger<AccumulatorsController> _logger;

    public AccumulatorsController(IAccumulatorService svc, ILogger<AccumulatorsController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    // ── canonical routes ────────────────────────────────────────────────

    [HttpGet("api/v1/accumulators/{memberId}")]
    [ProducesResponseType(typeof(AccumulatorResponse), 200)]
    public Task<IActionResult> Get(string memberId, [FromQuery] DateTime? asOfDate, CancellationToken ct)
        => GetCore(memberId, asOfDate, ct);

    [HttpGet("api/v1/accumulators/{memberId}/history")]
    [ProducesResponseType(typeof(AccumulatorHistoryResponse), 200)]
    public async Task<IActionResult> GetHistory(string memberId, CancellationToken ct)
    {
        var h = await _svc.GetHistoryAsync(TenantId, memberId, ct);
        return Ok(h);
    }

    [HttpPost("api/v1/accumulators/{memberId}/adjust")]
    [ProducesResponseType(typeof(AccumulatorAdjustmentResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Adjust(string memberId, [FromBody] AccumulatorAdjustmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (request.PlanYearStart > request.PlanYearEnd)
            return BadRequest("PlanYearStart must be <= PlanYearEnd");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("Reason is required for manual adjustment");

        var result = await _svc.AdjustAsync(TenantId, memberId, request, ct);
        _logger.LogInformation(
            "Accumulator adjusted: tenant={TenantId} member={MemberId} adjustmentId={AdjustmentId} actor={ActorId}",
            SanitizeForLog(TenantId),
            SanitizeForLog(memberId),
            SanitizeForLog(result.AdjustmentId),
            SanitizeForLog(request.ActorId));
        return Ok(result);
    }

    // ── compat alias for member-service ─────────────────────────────────

    [HttpGet("api/v1/members/{memberId}/accumulators")]
    [ProducesResponseType(typeof(AccumulatorResponse), 200)]
    public Task<IActionResult> GetForMember(string memberId, [FromQuery] DateTime? asOfDate, CancellationToken ct)
        => GetCore(memberId, asOfDate, ct);

    // ── shared ──────────────────────────────────────────────────────────

    private async Task<IActionResult> GetCore(string memberId, DateTime? asOfDate, CancellationToken ct)
    {
        var result = await _svc.GetAsync(TenantId, memberId, asOfDate, ct);
        if (result is null)
        {
            // No snapshot exists yet (new member or no claims in the requested plan year).
            // Return an empty zero-state rather than 404 so the portal renders clean.
            return Ok(new AccumulatorResponse
            {
                MemberId = memberId,
                PlanYearStart = asOfDate.HasValue ? new DateTime(asOfDate.Value.Year, 1, 1) : new DateTime(DateTime.UtcNow.Year, 1, 1),
                PlanYearEnd = asOfDate.HasValue ? new DateTime(asOfDate.Value.Year, 12, 31) : new DateTime(DateTime.UtcNow.Year, 12, 31)
            });
        }
        return Ok(result);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
