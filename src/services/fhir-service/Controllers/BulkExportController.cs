using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR Bulk Data Access endpoints for system-level and group-level export.
/// Implements the async polling pattern per the Bulk Data IG.
/// </summary>
[Route("fhir/r4")]
[Authorize]
[Produces("application/json")]
public class BulkExportController : FhirControllerBase
{
    private readonly IBulkExportService _exportService;
    private readonly ILogger<BulkExportController> _logger;

    public BulkExportController(
        IBulkExportService exportService,
        ILogger<BulkExportController> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    /// <summary>POST /fhir/r4/$export — initiate system-level bulk export</summary>
    [HttpPost("$export")]
    public async Task<IActionResult> SystemExport(
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
    {
        if (!HasRespondAsyncHeader())
            return FhirBadRequest("Bulk Data export requires 'Prefer: respond-async' header");

        var request = new BulkExportRequest
        {
            Type = type,
            Since = since,
            OutputFormat = outputFormat ?? "application/fhir+ndjson",
        };

        var job = await _exportService.InitiateExportAsync(request, TenantId, ct);

        _logger.LogInformation(
            "Bulk export initiated: job={JobId}, tenant={TenantId}",
            SanitizeForLog(job.JobId), SanitizeForLog(TenantId));

        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/$export-status/{job.JobId}";
        return StatusCode(202);
    }

    /// <summary>POST /fhir/r4/Group/{groupId}/$export — initiate group-level bulk export</summary>
    [HttpPost("Group/{groupId}/$export")]
    public async Task<IActionResult> GroupExport(
        string groupId,
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
    {
        if (!HasRespondAsyncHeader())
            return FhirBadRequest("Bulk Data export requires 'Prefer: respond-async' header");

        var request = new BulkExportRequest
        {
            Type = type,
            Since = since,
            OutputFormat = outputFormat ?? "application/fhir+ndjson",
            GroupId = groupId,
        };

        var job = await _exportService.InitiateExportAsync(request, TenantId, ct);

        _logger.LogInformation(
            "Group bulk export initiated: job={JobId}, group={GroupId}, tenant={TenantId}",
            SanitizeForLog(job.JobId), SanitizeForLog(groupId), SanitizeForLog(TenantId));

        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/$export-status/{job.JobId}";
        return StatusCode(202);
    }

    /// <summary>GET /fhir/r4/$export-status/{jobId} — poll export job status</summary>
    [HttpGet("$export-status/{jobId}")]
    public async Task<IActionResult> ExportStatus(string jobId, CancellationToken ct)
    {
        var job = await _exportService.GetJobStatusAsync(jobId, TenantId, ct);

        if (job == null)
            return NotFound(new { error = $"Export job '{jobId}' not found" });

        if (job.Status == BulkExportStatus.Complete && job.Manifest != null)
            return Ok(job.Manifest);

        if (job.Status == BulkExportStatus.Error)
        {
            return StatusCode(500, new { error = job.ErrorMessage ?? "Export failed" });
        }

        // Still in progress
        Response.Headers["X-Progress"] = $"{job.ProgressPercent}% complete";
        Response.Headers["Retry-After"] = "120";
        return StatusCode(202);
    }

    /// <summary>DELETE /fhir/r4/$export-status/{jobId} — cancel an export job</summary>
    [HttpDelete("$export-status/{jobId}")]
    public async Task<IActionResult> CancelExport(string jobId, CancellationToken ct)
    {
        var cancelled = await _exportService.CancelJobAsync(jobId, TenantId, ct);

        if (!cancelled)
            return NotFound(new { error = $"Export job '{jobId}' not found" });

        _logger.LogInformation("Bulk export job {JobId} cancelled by tenant {TenantId}",
            SanitizeForLog(jobId), SanitizeForLog(TenantId));

        return StatusCode(202);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool HasRespondAsyncHeader()
    {
        if (!Request.Headers.TryGetValue("Prefer", out var preferValues))
            return false;
        return preferValues.Any(v => v != null &&
            v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));
    }
}
