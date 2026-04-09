using Microsoft.AspNetCore.Mvc;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;

namespace EncounterSubmissionService.Controllers;

/// <summary>
/// API endpoints for querying and managing FMMIS encounter submissions.
/// The primary intake path is the Kafka adjudication-completed consumer;
/// these endpoints serve operational dashboards and manual intervention.
/// </summary>
[ApiController]
[Route("api/encounters")]
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
    /// Paginated list of pending encounter submissions for a tenant.
    /// Excludes accepted and permanently rejected (retries exhausted).
    /// Ordered by submission deadline ascending (most urgent first).
    /// </summary>
    [HttpGet("{tenantId}/pending")]
    [ProducesResponseType(typeof(IEnumerable<EncounterSubmission>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EncounterSubmission>>> GetPending(
        string tenantId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Fetching pending submissions for tenant {TenantId} (page {Page}, size {PageSize})",
            SanitizeForLog(tenantId), page, pageSize);

        var submissions = await _service.GetPendingSubmissionsAsync(tenantId, page, pageSize);
        return Ok(submissions);
    }

    /// <summary>
    /// Status counts by category for the encounter dashboard:
    /// pending, batched, submitted, accepted, partialAccept, rejected, deadlineWarning.
    /// </summary>
    [HttpGet("{tenantId}/summary")]
    [ProducesResponseType(typeof(EncounterStatusSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<EncounterStatusSummary>> GetSummary(string tenantId)
    {
        _logger.LogInformation(
            "Fetching encounter status summary for tenant {TenantId}",
            SanitizeForLog(tenantId));

        var summary = await _service.GetStatusSummaryAsync(tenantId);
        return Ok(summary);
    }

    /// <summary>
    /// Submissions within 7 days (configurable) of the 60-day FMMIS deadline.
    /// </summary>
    [HttpGet("{tenantId}/deadline-warnings")]
    [ProducesResponseType(typeof(IEnumerable<EncounterSubmission>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EncounterSubmission>>> GetDeadlineWarnings(
        string tenantId,
        [FromQuery] int warningDays = 7)
    {
        _logger.LogInformation(
            "Fetching deadline warnings for tenant {TenantId} (within {Days} days)",
            SanitizeForLog(tenantId), warningDays);

        var submissions = await _service.GetDeadlineWarningsAsync(tenantId, warningDays);
        return Ok(submissions);
    }

    /// <summary>
    /// Accept a raw X12 999 acknowledgment body and process it against the
    /// specified batch. Updates submission statuses (Accepted/PartialAccept/Rejected).
    /// </summary>
    [HttpPost("{tenantId}/acknowledge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessAcknowledgment(
        string tenantId,
        [FromBody] AcknowledgmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId))
        {
            return BadRequest(new { message = "BatchId is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { message = "999 acknowledgment content is required" });
        }

        _logger.LogInformation(
            "Processing 999 acknowledgment for batch {BatchId}, tenant {TenantId}",
            SanitizeForLog(request.BatchId), SanitizeForLog(tenantId));

        await _service.ProcessAcknowledgmentAsync(request.BatchId, request.Content, tenantId);
        return Ok(new { message = $"Acknowledgment processed for batch {request.BatchId}" });
    }

    /// <summary>
    /// Manually retry a rejected submission. Resets status to Pending so it
    /// is picked up in the next batch cycle. Fails if retries are exhausted.
    /// </summary>
    [HttpPost("{tenantId}/retry/{submissionId}")]
    [ProducesResponseType(typeof(EncounterSubmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EncounterSubmission>> RetrySubmission(
        string tenantId,
        string submissionId)
    {
        _logger.LogInformation(
            "Manual retry requested for submission {SubmissionId}, tenant {TenantId}",
            SanitizeForLog(submissionId), SanitizeForLog(tenantId));

        try
        {
            var submission = await _service.RetrySubmissionAsync(submissionId, tenantId);
            return Ok(submission);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Request body for processing a 999 acknowledgment.
/// </summary>
public class AcknowledgmentRequest
{
    /// <summary>
    /// The FMMIS batch ID to apply this acknowledgment to.
    /// </summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>
    /// Raw X12 999 acknowledgment content from FMMIS.
    /// </summary>
    public string Content { get; set; } = string.Empty;
}
