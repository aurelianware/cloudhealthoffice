using Microsoft.AspNetCore.Mvc;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;

namespace EncounterSubmissionService.Controllers;

/// <summary>
/// API endpoints for querying and managing FMMIS encounter submissions.
/// The primary intake path is the Kafka adjudication-completed consumer;
/// these endpoints are for operational visibility and manual intervention.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class EncounterSubmissionController : ControllerBase
{
    private readonly IEncounterSubmissionService _service;
    private readonly ILogger<EncounterSubmissionController> _logger;

    public EncounterSubmissionController(
        IEncounterSubmissionService service,
        ILogger<EncounterSubmissionController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Get an encounter submission by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(EncounterSubmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncounterSubmission>> GetById(string id)
    {
        var submission = await _service.GetByIdAsync(id);
        if (submission is null)
        {
            return NotFound(new { message = $"Encounter submission '{id}' not found" });
        }
        return Ok(submission);
    }

    /// <summary>
    /// Get all pending encounter submissions for a tenant.
    /// </summary>
    [HttpGet("tenant/{tenantId}/pending")]
    [ProducesResponseType(typeof(IEnumerable<EncounterSubmission>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EncounterSubmission>>> GetPending(string tenantId)
    {
        var submissions = await _service.GetPendingByTenantAsync(tenantId);
        return Ok(submissions);
    }

    /// <summary>
    /// Get encounter submissions approaching their 60-day deadline.
    /// </summary>
    [HttpGet("approaching-deadline")]
    [ProducesResponseType(typeof(IEnumerable<EncounterSubmission>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EncounterSubmission>>> GetApproachingDeadline(
        [FromQuery] int warningDays = 7)
    {
        var submissions = await _service.GetApproachingDeadlineAsync(warningDays);
        return Ok(submissions);
    }

    /// <summary>
    /// Process a 999 acknowledgment for a batch.
    /// </summary>
    [HttpPost("batch/{batchId}/acknowledgment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessAcknowledgment(
        string batchId,
        [FromBody] AcknowledgmentRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await _service.ProcessAcknowledgmentAsync(batchId, request.AcknowledgmentCode, request.Errors);
        return Ok(new { message = $"Acknowledgment processed for batch {batchId}" });
    }
}

/// <summary>
/// Request body for processing a 999 acknowledgment.
/// </summary>
public class AcknowledgmentRequest
{
    public string AcknowledgmentCode { get; set; } = string.Empty;
    public List<string>? Errors { get; set; }
}
