using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using EnrollmentImportService.Services.Runs;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace EnrollmentImportService.Controllers;

[ApiController]
[Route("api/v1/enrollment")]
public class EnrollmentController : ControllerBase
{
    private readonly IEnrollmentImportService _importService;
    private readonly IPlanCodeGapReportService _gapReportService;
    private readonly IEnrollmentImportRunRepository _importRuns;
    private readonly ICurrentActor _actor;
    private readonly ILogger<EnrollmentController> _logger;
    private readonly Raw834Intake? _raw834;
    private readonly IEnrollmentImportUnitStore? _units;
    private readonly IStreaming834Parser _streamingParser;

    public EnrollmentController(
        IEnrollmentImportService importService,
        IEnrollment834EdiParser ediParser,
        IPlanCodeGapReportService gapReportService,
        IEnrollmentImportRunRepository importRuns,
        ICurrentActor actor,
        ILogger<EnrollmentController> logger,
        Raw834Intake? raw834 = null,
        IStreaming834Parser? streamingParser = null,
        IEnrollmentImportUnitStore? units = null)
    {
        _units = units;
        _raw834 = raw834;
        _streamingParser = streamingParser ?? new Streaming834Parser();
        _importService = importService;
        _gapReportService = gapReportService;
        _importRuns = importRuns;
        _actor = actor;
        _logger = logger;
    }

    // Tenant from the validated token (shared TenantMiddleware via UseChoAuthentication).
    // X-Tenant-ID is only accepted when it agrees with the token, and is never read here.
    private string TenantId => _actor.TenantId;

    [HttpPost("import")]
    public async Task<ActionResult<ImportResult>> ImportEnrollment(
        [FromBody] Enrollment834 enrollment)
    {
        var tenantId = TenantId;

        _logger.LogInformation("Importing 834 file {FileName} for tenant {TenantId} with {Count} enrollments",
            LogSanitizer.Sanitize(enrollment.FileName), LogSanitizer.Sanitize(tenantId), enrollment.TransactionCount);

        // The importing user is the token subject; nothing in the body can claim it.
        enrollment.ActorId = _actor.UserId;
        var result = await _importService.ImportEnrollmentAsync(enrollment, tenantId);

        return Ok(result);
    }

    /// <summary>
    /// Accepts a raw X12 834 file of any size — the one intake path for
    /// uploads and SFTP files alike (edi-operations.md §11). The file is
    /// streamed to the raw-file store, its envelope checked (TA1, duplicate
    /// ISA13), its EDI file record written, and an import run queued. The
    /// request then waits up to <c>EnrollmentImport:Runs:WaitSeconds</c>: a run
    /// that finishes in time answers 200 with the <see cref="ImportResult"/>;
    /// otherwise 202 with the run and file ids (<c>GET import/runs/{id}</c>).
    /// </summary>
    [HttpPost("import/raw834")]
    [RequestSizeLimit(MaxRawUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRawUploadBytes)]
    public async Task<ActionResult<ImportResult>> ImportRaw834(
        [FromForm] IFormFile file,
        [FromForm] string? source = null,
        [FromForm] string? sourcePath = null)
    {
        var tenantId = TenantId;

        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty 834 file is required.");
        }
        if (_raw834 is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Raw 834 intake is not configured.");
        }

        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        Raw834IntakeOutcome outcome;
        await using (var content = file.OpenReadStream())
        {
            outcome = await _raw834.ReceiveAsync(tenantId, _actor.UserId, content, file.FileName, source, sourcePath, ct);
        }

        if (outcome.IsRejected)
        {
            var envelope = outcome.Envelope!;
            return BadRequest(new
            {
                error = "The interchange envelope (ISA/IEA) was rejected: " + string.Join("; ", envelope.RejectionReasons),
                ediFileId = outcome.EdiFile?.Id,
                interchangeAcknowledgmentCode = Ta1AckCodes.Rejected,
                acknowledgmentTa1 = envelope.Ta1,
                ta1AcknowledgmentIds = envelope.AcknowledgmentIds,
            });
        }

        // The wait is not tied to the request: a caller that hangs up still gets its run.
        var run = await _raw834.WaitAsync(outcome.Run!, CancellationToken.None);
        return RunResponse(run);
    }

    /// <summary>The largest raw 834 accepted (1 GiB); the file is streamed, never held in memory.</summary>
    public const long MaxRawUploadBytes = 1L << 30;

    /// <summary>One import run: its status, progress and, once finished, its counts.</summary>
    [HttpGet("import/runs/{id}")]
    [ProducesResponseType(typeof(EnrollmentImportRunStatusResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetImportRun(string id, CancellationToken ct)
    {
        var run = await _importRuns.GetAsync(TenantId, id, ct);
        if (run is null) return NotFound();
        var failed = run.FailedUnits > 0 && _units is not null ? await _units.ListFailedAsync(run.TenantId, run.Id, 100, ct) : null;
        return Ok(EnrollmentImportRunStatusResponse.From(run, failed));
    }

    private ActionResult<ImportResult> RunResponse(EnrollmentImportRun run)
    {
        if (run.Status == EnrollmentImportRunStatus.Completed)
            return Ok(ToImportResult(run));

        if (run.Status == EnrollmentImportRunStatus.Failed)
        {
            return run.FailureKind == EnrollmentImportFailureKinds.Parse
                ? BadRequest(run.FailureMessage)
                : StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    error = run.FailureMessage,
                    importRunId = run.Id,
                    ediFileId = run.EdiFileId,
                });
        }

        return Accepted($"api/v1/enrollment/import/runs/{run.Id}", EnrollmentImportRunStatusResponse.From(run));
    }

    private static ImportResult ToImportResult(EnrollmentImportRun run) => new()
    {
        FileName = run.FileName ?? string.Empty,
        BatchId = run.BatchId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        SuccessCount = run.SuccessCount,
        FailedCount = run.FailedCount,
        SkippedCount = run.SkippedCount,
        MembersCreated = run.MembersCreated,
        MembersUpdated = run.MembersUpdated,
        MembersTerminated = run.MembersTerminated,
        DependentsCreated = run.DependentsCreated,
        DependentsUpdated = run.DependentsUpdated,
        DependentsTerminated = run.DependentsTerminated,
        DependentsFailed = run.DependentsFailed,
        CoverageRecordsCreated = run.CoverageRecordsCreated,
        CoverageRecordsUpdated = run.CoverageRecordsUpdated,
        CoverageRecordsReinstated = run.CoverageRecordsReinstated,
        CoverageRecordsTerminated = run.CoverageRecordsTerminated,
        CoverageMappingsUnresolved = run.CoverageMappingsUnresolved,
        Errors = run.Errors,
        EdiFileId = run.EdiFileId,
        InterchangeAcknowledgmentCode = run.InterchangeAcknowledgmentCode,
        AcknowledgmentTa1 = run.AcknowledgmentTa1,
        Ta1AcknowledgmentIds = run.Ta1AcknowledgmentIds,
    };

    /// <summary>
    /// Read-only onboarding check: scans an already-structured 834 batch for
    /// every distinct plan code it uses and reports which are already mapped
    /// in benefit-plan-service's plan-code-mapping crosswalk vs. still
    /// missing. Makes no writes — safe to run repeatedly against a trading
    /// partner's test file while filling in the gaps before go-live.
    /// </summary>
    [HttpPost("plan-code-gap-report")]
    // Read-only: resolves plan codes and writes nothing.
    [RequirePermission("enrollment:read")]
    public async Task<ActionResult<PlanCodeGapReport>> PlanCodeGapReport(
        [FromBody] Enrollment834 enrollment,
        CancellationToken ct)
    {
        var tenantId = TenantId;

        var report = await _gapReportService.BuildReportAsync(enrollment, tenantId, ct);
        return Ok(report);
    }

    /// <summary>Same as <see cref="PlanCodeGapReport"/>, but for a raw X12 834 file upload.</summary>
    [HttpPost("plan-code-gap-report/raw834")]
    [RequirePermission("enrollment:read")]
    [RequestSizeLimit(MaxRawUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRawUploadBytes)]
    public async Task<ActionResult<PlanCodeGapReport>> PlanCodeGapReportRaw834(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        var tenantId = TenantId;

        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty 834 file is required.");
        }

        // Streamed unit by unit: only the plan codes are kept.
        try
        {
            await using var content = file.OpenReadStream();
            var units = _streamingParser.ParseAsync(content, file.FileName, ct: ct);
            var report = await _gapReportService.BuildReportAsync(units, file.FileName, tenantId, ct);
            return Ok(report);
        }
        catch (X12FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded 834 file {FileName} for tenant {TenantId}",
                LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId));
            return BadRequest($"Could not parse 834 file: {ex.Message}");
        }
    }

    /// <summary>
    /// Most recent 834 import runs for the tenant, newest first — one row
    /// per batch (raw834 upload or structured /import call), with the same
    /// counts <see cref="ImportResult"/> already returns synchronously. The
    /// admin-console read path for "what happened the last time this
    /// employer's file was dropped," without needing to have watched the
    /// original API response.
    /// </summary>
    [HttpGet("import-runs")]
    [ProducesResponseType(typeof(List<EnrollmentImportRun>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ListImportRuns(
        [FromQuery] int limit = 100)
    {
        var tenantId = TenantId;
        if (limit < 1 || limit > 500) limit = 100;

        var runs = await _importRuns.ListRecentAsync(tenantId, limit);
        return Ok(runs);
    }

    /// <summary>Static liveness echo; returns no tenant data.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "enrollment-import" });
    }
}
