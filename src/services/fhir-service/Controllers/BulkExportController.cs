using FhirService.Services.Identity;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ProviderAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Controllers;

/// <summary>
/// FHIR Bulk Data Access endpoints for system-level and group-level export.
/// The default service explicitly refuses initiation until durable export exists.
/// With <c>BulkExport:Enabled</c>, a provider exports its own attributed panel
/// as <c>Group/{its provider id}/$export</c> (G-34); a system-level export is
/// still refused.
///
/// Every job belongs to the caller that started it. Another caller, in the same
/// tenant or not, is answered as if the job did not exist, whether it polls,
/// cancels or downloads.
/// </summary>
[Route("fhir/r4")]
// DELIBERATELY SMART/system-only. A bulk export covers every member in its
// scope, and no CHO permission has been approved for that: staff (CHO) tokens,
// TenantAdmin included, are refused here. Do not map this to members:read,
// clinical:read or any existing permission; it stays cho: null until a
// dedicated `bulk-export` permission is approved.
[FhirAccess(smart: true, cho: null)]
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
            RequestUrl = $"{FhirBaseUrl}/$export{RawQueryString}",
            RequestedBy = CallerId,
        };

        BulkExportJob job;
        try
        {
            job = await _exportService.InitiateExportAsync(request, TenantId, ct);
        }
        catch (BulkExportNotImplementedException)
        {
            return BulkExportNotImplemented();
        }
        catch (BulkExportRequestException ex)
        {
            return FhirBadRequest(ex.Message);
        }

        _logger.LogInformation(
            "Bulk export initiated: job={JobId}, tenant={TenantId}",
            LogSanitizer.Sanitize(job.JobId), LogSanitizer.Sanitize(TenantId));

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
            RequestUrl = $"{FhirBaseUrl}/Group/{Uri.EscapeDataString(groupId)}/$export{RawQueryString}",
            RequestedBy = CallerId,
        };

        BulkExportJob job;
        try
        {
            job = await _exportService.InitiateExportAsync(request, TenantId, ct);
        }
        catch (BulkExportNotImplementedException)
        {
            return BulkExportNotImplemented();
        }
        catch (BulkExportRequestException ex)
        {
            return FhirBadRequest(ex.Message);
        }
        catch (BulkExportGroupNotFoundException)
        {
            return FhirNotFound("Group", groupId);
        }

        _logger.LogInformation(
            "Group bulk export initiated: job={JobId}, group={GroupId}, tenant={TenantId}",
            LogSanitizer.Sanitize(job.JobId), LogSanitizer.Sanitize(groupId), LogSanitizer.Sanitize(TenantId));

        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/$export-status/{job.JobId}";
        return StatusCode(202);
    }

    /// <summary>GET /fhir/r4/$export-status/{jobId} — poll export job status</summary>
    [HttpGet("$export-status/{jobId}")]
    public async Task<IActionResult> ExportStatus(string jobId, CancellationToken ct)
    {
        var job = await OwnJobAsync(jobId, ct);

        if (job == null)
            return NotFound(new { error = $"Export job '{jobId}' not found" });

        if (job.Status == BulkExportStatus.Complete)
        {
            if (job.ExpiresAt is { } expires)
                Response.Headers["Expires"] = expires.ToString("R");
            return Ok(job.Manifest ?? BuildManifest(job));
        }

        if (job.Status == BulkExportStatus.Error)
        {
            return StatusCode(500, new { error = job.ErrorMessage ?? "Export failed" });
        }

        if (job.Status == BulkExportStatus.Cancelled)
            return StatusCode(StatusCodes.Status410Gone);

        // Still in progress
        Response.Headers["X-Progress"] = $"{job.ProgressPercent}% complete";
        Response.Headers["Retry-After"] = "120";
        return StatusCode(202);
    }

    /// <summary>DELETE /fhir/r4/$export-status/{jobId} — cancel an export job</summary>
    [HttpDelete("$export-status/{jobId}")]
    public async Task<IActionResult> CancelExport(string jobId, CancellationToken ct)
    {
        var cancelled = await OwnJobAsync(jobId, ct) is not null
            && await _exportService.CancelJobAsync(jobId, TenantId, ct);

        if (!cancelled)
            return NotFound(new { error = $"Export job '{jobId}' not found" });

        _logger.LogInformation("Bulk export job {JobId} cancelled by tenant {TenantId}",
            LogSanitizer.Sanitize(jobId), LogSanitizer.Sanitize(TenantId));

        return StatusCode(202);
    }

    /// <summary>
    /// GET /fhir/r4/$export-file/{jobId}/{fileName} — one NDJSON file of a
    /// completed export. Served through fhir-service with the caller's token,
    /// never as a link into storage (the manifest says
    /// <c>requiresAccessToken: true</c>).
    /// </summary>
    [HttpGet("$export-file/{jobId}/{fileName}")]
    public async Task<IActionResult> ExportFile(string jobId, string fileName, CancellationToken ct)
    {
        var job = await OwnJobAsync(jobId, ct);
        var content = job is null ? null : await _exportService.OpenFileAsync(jobId, fileName, TenantId, ct);
        if (content is null)
            return NotFound(new { error = $"Export file '{fileName}' not found" });

        Response.Headers.CacheControl = "no-store";
        return File(content, "application/fhir+ndjson");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The validated caller, as Provider Access identifies it.</summary>
    private string? CallerId => ProviderAccessAuthorizationFilter.ResolveCallerId(HttpContext);

    /// <summary>The job, only when this caller started it.</summary>
    private async Task<BulkExportJob?> OwnJobAsync(string jobId, CancellationToken ct)
    {
        var job = await _exportService.GetJobStatusAsync(jobId, TenantId, ct);
        var caller = CallerId;
        return job is not null
               && !string.IsNullOrEmpty(caller)
               && string.Equals(job.RequestedBy, caller, StringComparison.Ordinal)
            ? job
            : null;
    }

    private BulkExportManifest BuildManifest(BulkExportJob job) => new()
    {
        TransactionTime = (job.TransactionTime ?? job.CreatedAt).ToString("o"),
        Request = job.Request,
        RequiresAccessToken = true,
        Output = job.Files
            .Select(f => new BulkExportOutput
            {
                Type = f.Type,
                Url = $"{FhirBaseUrl}/$export-file/{job.JobId}/{Uri.EscapeDataString(f.Name)}",
                Count = f.Count,
            })
            .ToList(),
    };

    private IActionResult BulkExportNotImplemented()
    {
        var result = new ObjectResult(new Hl7.Fhir.Model.OperationOutcome
        {
            Issue = new List<Hl7.Fhir.Model.OperationOutcome.IssueComponent>
            {
                new()
                {
                    Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                    Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported,
                    Diagnostics = "Bulk export is not implemented. No export job or download files were created.",
                },
            },
        }) { StatusCode = StatusCodes.Status501NotImplemented };
        result.ContentTypes.Add("application/fhir+json");
        return result;
    }

    private bool HasRespondAsyncHeader()
    {
        if (!Request.Headers.TryGetValue("Prefer", out var preferValues))
            return false;
        return preferValues.Any(v => v != null &&
            v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));
    }
}
