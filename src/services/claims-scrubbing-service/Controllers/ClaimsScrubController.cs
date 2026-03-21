using ClaimsScrubbingService.Models;
using ClaimsScrubbingService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsScrubbingService.Controllers;

[ApiController]
[Route("api")]
[Produces("application/json")]
public class ClaimsScrubController : ControllerBase
{
    private readonly IClaimsScrubberService _scrubber;
    private readonly IValidationRuleEngine _ruleEngine;
    private readonly ILogger<ClaimsScrubController> _logger;

    public ClaimsScrubController(
        IClaimsScrubberService scrubber,
        IValidationRuleEngine ruleEngine,
        ILogger<ClaimsScrubController> logger)
    {
        _scrubber   = scrubber;
        _ruleEngine = ruleEngine;
        _logger     = logger;
    }

    /// <summary>Validate a single 837 claim against all applicable rules.</summary>
    [HttpPost("claims/validate")]
    [ProducesResponseType(typeof(ValidateClaimResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateClaim([FromBody] ValidateClaimRequest request)
    {
        if (request?.Claim == null)
            return BadRequest(new { error = "Claim is required" });

        // Propagate correlation ID from header if not already set in body
        if (string.IsNullOrEmpty(request.CorrelationId))
        {
            request.CorrelationId = Request.Headers["X-Correlation-Id"].FirstOrDefault()
                                    ?? Guid.NewGuid().ToString();
        }

        _logger.LogInformation("Validating claim {ClaimId} (type={ClaimType}, correlationId={CorrelationId})",
            request.Claim.ClaimId, request.Claim.ClaimType, request.CorrelationId);

        var response = await _scrubber.ValidateClaimAsync(request);
        return Ok(response);
    }

    /// <summary>Validate a batch of 837 claims.</summary>
    [HttpPost("claims/validate/batch")]
    [ProducesResponseType(typeof(BatchValidateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateBatch([FromBody] BatchValidateRequest request)
    {
        if (request?.Claims == null || request.Claims.Count == 0)
            return BadRequest(new { error = "At least one claim is required" });

        if (string.IsNullOrEmpty(request.CorrelationId))
        {
            request.CorrelationId = Request.Headers["X-Correlation-Id"].FirstOrDefault()
                                    ?? Guid.NewGuid().ToString();
        }

        _logger.LogInformation("Validating batch of {Count} claim(s) (correlationId={CorrelationId})",
            request.Claims.Count, request.CorrelationId);

        var response = await _scrubber.ValidateBatchAsync(request);
        return Ok(response);
    }

    /// <summary>List all registered validation rules.</summary>
    [HttpGet("rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRules()
    {
        var rules = _ruleEngine.GetRules();
        return Ok(new { rules, count = rules.Count });
    }

    /// <summary>List rules filtered by category.</summary>
    [HttpGet("rules/category/{category}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRulesByCategory(string category)
    {
        var rules = _ruleEngine.GetRulesByCategory(category);
        return Ok(new { rules, count = rules.Count, category });
    }

    /// <summary>Service processing metrics.</summary>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(ServiceMetrics), StatusCodes.Status200OK)]
    public IActionResult GetMetrics()
    {
        return Ok(_scrubber.GetMetrics());
    }
}
