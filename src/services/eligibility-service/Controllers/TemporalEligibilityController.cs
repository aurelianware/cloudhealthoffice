using Microsoft.AspNetCore.Mvc;
using EligibilityService.Models;
using EligibilityService.Services;

namespace EligibilityService.Controllers;

/// <summary>
/// Temporal eligibility read projection.
/// GET /api/v1/eligibility/temporal?memberId=X&serviceDate=YYYY-MM-DD
/// returns every coverage active on the queried date with COB order and
/// accumulator snapshot.
/// </summary>
[ApiController]
[Route("api/v1/eligibility/temporal")]
public class TemporalEligibilityController : ControllerBase
{
    private readonly ITemporalEligibilityService _temporal;
    private readonly ILogger<TemporalEligibilityController> _logger;

    public TemporalEligibilityController(
        ITemporalEligibilityService temporal,
        ILogger<TemporalEligibilityController> logger)
    {
        _temporal = temporal;
        _logger = logger;
    }

    private string TenantId => HttpContext.Items["TenantId"]?.ToString() ?? string.Empty;

    [HttpGet]
    [ProducesResponseType(typeof(TemporalEligibilityResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TemporalEligibilityResult>> Get(
        [FromQuery] string memberId,
        [FromQuery] DateTime serviceDate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        if (serviceDate == default)
            return BadRequest(new { error = "serviceDate is required (YYYY-MM-DD)" });

        _logger.LogInformation(
            "Temporal eligibility lookup for member {Member} on {Date}",
            SanitizeForLog(memberId), serviceDate.ToString("yyyy-MM-dd"));

        var result = await _temporal.GetAsOfAsync(TenantId, memberId, serviceDate, ct);
        return Ok(result);
    }

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
