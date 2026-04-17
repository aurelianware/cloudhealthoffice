using Microsoft.AspNetCore.Mvc;
using EligibilityService.Models;
using EligibilityService.Services;

namespace EligibilityService.Controllers;

/// <summary>
/// Batch eligibility verification.
///   POST /api/v1/eligibility/batch            — submit CSV or JSON, returns 202 + jobId
///   GET  /api/v1/eligibility/batch/{jobId}    — poll status / result URL
///   GET  /api/v1/eligibility/batch/{jobId}/result — download the CSV result file
/// </summary>
[ApiController]
[Route("api/v1/eligibility/batch")]
public class BatchEligibilityController : ControllerBase
{
    private readonly IBatchEligibilityService _batch;
    private readonly ILogger<BatchEligibilityController> _logger;

    public BatchEligibilityController(
        IBatchEligibilityService batch,
        ILogger<BatchEligibilityController> logger)
    {
        _batch = batch;
        _logger = logger;
    }

    private string TenantId => HttpContext.Items["TenantId"]?.ToString() ?? string.Empty;

    [HttpPost]
    [Consumes("text/csv", "application/json")]
    [ProducesResponseType(typeof(BatchEligibilityJob), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(CancellationToken ct)
    {
        BatchEligibilityJob job;
        try
        {
            job = await _batch.SubmitAsync(
                TenantId, Request.Body, Request.ContentType ?? "text/csv", ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        Response.Headers["Location"] = $"/api/v1/eligibility/batch/{job.Id}";
        return StatusCode(StatusCodes.Status202Accepted, job);
    }

    [HttpGet("{jobId}")]
    [ProducesResponseType(typeof(BatchEligibilityJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string jobId, CancellationToken ct)
    {
        var job = await _batch.GetJobAsync(TenantId, jobId, ct);
        if (job == null)
            return NotFound(new { jobId, error = "Job not found" });
        return Ok(job);
    }

    [HttpGet("{jobId}/result")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Download(string jobId, CancellationToken ct)
    {
        var job = await _batch.GetJobAsync(TenantId, jobId, ct);
        if (job == null)
            return NotFound(new { jobId, error = "Job not found" });
        if (job.Status != BatchJobStatus.Completed)
            return Conflict(new { jobId, status = job.Status.ToString(), error = "Job not yet complete" });

        var payload = await _batch.GetResultAsync(TenantId, jobId, ct);
        if (payload == null)
            return NotFound(new { jobId, error = "Result file not available" });

        Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"eligibility-batch-{jobId}.csv\"";
        return File(payload, "text/csv");
    }
}
