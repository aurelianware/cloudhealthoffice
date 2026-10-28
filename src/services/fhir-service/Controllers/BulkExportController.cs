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
/// as <c>Group/{its provider id}/$export</c> or <c>Patient/$export</c> (G-34);
/// a system-level export is still refused.
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

    /// <summary>GET or POST /fhir/r4/$export — initiate system-level bulk export</summary>
    [HttpGet("$export")]
    [HttpPost("$export")]
    public Task<IActionResult> SystemExport(
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
        => KickOffAsync(BulkExportLevel.System, groupId: null, type, since, outputFormat, ct);

    /// <summary>
    /// GET or POST /fhir/r4/Patient/$export — initiate patient-level bulk
    /// export: every patient the caller may export (for a provider, its
    /// attributed panel).
    /// </summary>
    [HttpGet("Patient/$export")]
    [HttpPost("Patient/$export")]
    public Task<IActionResult> PatientExport(
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
        => KickOffAsync(BulkExportLevel.Patient, groupId: null, type, since, outputFormat, ct);

    /// <summary>GET or POST /fhir/r4/Group/{groupId}/$export — initiate group-level bulk export</summary>
    [HttpGet("Group/{groupId}/$export")]
    [HttpPost("Group/{groupId}/$export")]
    public Task<IActionResult> GroupExport(
        string groupId,
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
        => KickOffAsync(BulkExportLevel.Group, groupId, type, since, outputFormat, ct);

    /// <summary>
    /// GET or POST /fhir/r4/PayerToPayer/$export — the Payer-to-Payer bulk
    /// transfer (G-35): every member whose consent naming the requesting payer
    /// is in force (recorded by <c>$member-match</c> or
    /// <c>$member-data-export</c>, ADR 022), each exported as
    /// <c>$member-data-export</c> would. Backend payer clients only, bound to
    /// the payer they are registered to, exactly as the single-member export.
    /// </summary>
    [HttpGet("PayerToPayer/$export")]
    [HttpPost("PayerToPayer/$export")]
    public Task<IActionResult> PayerToPayerExport(
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
    {
        if (!IsBackendSystemCaller)
            return Task.FromResult(FhirForbidden(PayerToPayerController.BackendOnlyMessage));
        if (!TryBindReceivingPayer(null, _logger, "bulk export", out var receivingPayerId))
            return Task.FromResult(FhirForbidden(PayerToPayerController.PayerBindingMessage));

        return KickOffAsync(BulkExportLevel.PayerToPayer, groupId: null, type, since, outputFormat, ct, receivingPayerId);
    }

    /// <summary>
    /// GET or POST /fhir/r4/Group/{groupId}/$davinci-data-export — the PDex
    /// Payer-to-Payer bulk export of a Group <c>$bulk-member-match</c> answered
    /// with (G-35). Only the matched Group of the client that asked for it,
    /// with <c>exportType=hl7.fhir.us.davinci-pdex#payertopayer</c>; each member
    /// is exported as <c>PayerToPayer/$export</c> exports it, consent judged
    /// again when the export runs. <c>_until</c> and <c>_typeFilter</c> are
    /// refused rather than ignored.
    /// </summary>
    [HttpGet("Group/{groupId}/$davinci-data-export")]
    [HttpPost("Group/{groupId}/$davinci-data-export")]
    public Task<IActionResult> DavinciDataExport(
        string groupId,
        [FromQuery(Name = "exportType")] string? exportType,
        [FromQuery(Name = "_type")] string? type,
        [FromQuery(Name = "_since")] string? since,
        [FromQuery(Name = "_until")] string? until,
        [FromQuery(Name = "_typeFilter")] string? typeFilter,
        [FromQuery(Name = "_outputFormat")] string? outputFormat,
        CancellationToken ct)
    {
        if (!IsBackendSystemCaller)
            return Task.FromResult(FhirForbidden(PayerToPayerController.BackendOnlyMessage));
        if (!TryBindReceivingPayer(null, _logger, "bulk export", out var receivingPayerId))
            return Task.FromResult(FhirForbidden(PayerToPayerController.PayerBindingMessage));

        if (!string.Equals(exportType, PayerToPayerExportType, StringComparison.Ordinal))
            return Task.FromResult(FhirBadRequest($"exportType must be {PayerToPayerExportType}."));
        if (!string.IsNullOrEmpty(until) || !string.IsNullOrEmpty(typeFilter))
            return Task.FromResult(FhirBadRequest("_until and _typeFilter are not supported."));

        return KickOffAsync(BulkExportLevel.PayerToPayer, groupId, type, since, outputFormat, ct, receivingPayerId,
            path: $"Group/{Uri.EscapeDataString(groupId)}/$davinci-data-export");
    }

    /// <summary>The PDex export type of a Payer-to-Payer <c>$davinci-data-export</c>.</summary>
    public const string PayerToPayerExportType = "hl7.fhir.us.davinci-pdex#payertopayer";

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

        // Bulk Data 2.0: an error status carries an OperationOutcome, and a
        // cancelled export's status URL is 404 from the moment it is deleted.
        if (job.Status == BulkExportStatus.Error)
        {
            return StatusCode(500, new Hl7.Fhir.Model.OperationOutcome
            {
                Issue =
                [
                    new Hl7.Fhir.Model.OperationOutcome.IssueComponent
                    {
                        Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                        Code = Hl7.Fhir.Model.OperationOutcome.IssueType.Exception,
                        Diagnostics = job.ErrorMessage ?? "Export failed",
                    },
                ],
            });
        }

        if (job.Status == BulkExportStatus.Cancelled)
            return FhirNotFound("$export-status", jobId);

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

    /// <summary>
    /// One kick-off, whichever level: the Bulk Data IG's GET, or POST with the
    /// parameters on the query string. Answers 202 with the status URL.
    /// </summary>
    private async Task<IActionResult> KickOffAsync(
        BulkExportLevel level, string? groupId, string? type, string? since, string? outputFormat, CancellationToken ct,
        string? receivingPayerId = null, string? path = null)
    {
        if (!HasRespondAsyncHeader())
            return FhirBadRequest("Bulk Data export requires 'Prefer: respond-async' header");

        path ??= level switch
        {
            BulkExportLevel.Group => $"Group/{Uri.EscapeDataString(groupId!)}/$export",
            BulkExportLevel.Patient => "Patient/$export",
            BulkExportLevel.PayerToPayer => "PayerToPayer/$export",
            _ => "$export",
        };

        var request = new BulkExportRequest
        {
            Level = level,
            Type = type,
            Since = since,
            OutputFormat = outputFormat ?? "application/fhir+ndjson",
            GroupId = groupId,
            ReceivingPayerId = receivingPayerId,
            RequestUrl = $"{FhirBaseUrl}/{path}{RawQueryString}",
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
            return FhirNotFound("Group", groupId ?? string.Empty);
        }

        _logger.LogInformation(
            "Bulk export initiated: job={JobId}, level={Level}, tenant={TenantId}",
            LogSanitizer.Sanitize(job.JobId), level, LogSanitizer.Sanitize(TenantId));

        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/$export-status/{job.JobId}";
        return StatusCode(202);
    }

    /// <summary>The validated caller, as Provider Access identifies it.</summary>
    private string? CallerId => ProviderAccessAuthorizationFilter.ResolveCallerId(HttpContext);

    /// <summary>
    /// The job, only when this caller started it. A Payer-to-Payer export is
    /// also the requesting payer's: the caller must still be registered to
    /// that payer.
    /// </summary>
    private async Task<BulkExportJob?> OwnJobAsync(string jobId, CancellationToken ct)
    {
        var job = await _exportService.GetJobStatusAsync(jobId, TenantId, ct);
        var caller = CallerId;
        if (job is null
            || string.IsNullOrEmpty(caller)
            || !string.Equals(job.RequestedBy, caller, StringComparison.Ordinal))
            return null;

        if (job.Level == BulkExportLevel.PayerToPayer
            && (job.ReceivingPayerId is null
                || !string.Equals(RegisteredPayerId, job.ReceivingPayerId, StringComparison.Ordinal)))
            return null;

        return job;
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
