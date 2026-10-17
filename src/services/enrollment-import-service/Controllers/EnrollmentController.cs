using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
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
    private readonly IEnrollment834EdiParser _ediParser;
    private readonly IPlanCodeGapReportService _gapReportService;
    private readonly IEnrollmentImportRunRepository _importRuns;
    private readonly ICurrentActor _actor;
    private readonly ILogger<EnrollmentController> _logger;
    private readonly IX12InterchangeIntake? _interchangeIntake;

    /// <summary>
    /// Set once the raw 834 reached ImportEnrollmentAsync. The import writes
    /// members, coverage and transaction rows as it goes, and a replay is not
    /// guaranteed to be a no-op, so from then on the ISA13 is never released.
    /// </summary>
    private bool _raw834ImportStarted;

    public EnrollmentController(
        IEnrollmentImportService importService,
        IEnrollment834EdiParser ediParser,
        IPlanCodeGapReportService gapReportService,
        IEnrollmentImportRunRepository importRuns,
        ICurrentActor actor,
        ILogger<EnrollmentController> logger,
        IX12InterchangeIntake? interchangeIntake = null,
        Microsoft.Extensions.Options.IOptions<X12InterchangeOptions>? interchangeOptions = null)
    {
        _interchangeIntake = interchangeOptions?.Value.Enabled == false ? null : interchangeIntake;
        _importService = importService;
        _ediParser = ediParser;
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
    /// Accepts a raw X12 834 EDI file, parses it, and imports it — the
    /// on-ramp for evaluators dropping in their own enrollment file rather
    /// than calling /import with an already-structured payload.
    /// </summary>
    [HttpPost("import/raw834")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<ImportResult>> ImportRaw834(
        [FromForm] IFormFile file)
    {
        var tenantId = TenantId;

        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty 834 file is required.");
        }

        string ediContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            ediContent = await reader.ReadToEndAsync();
        }

        // Interchange envelope first: a rejected ISA/IEA gets a TA1 and its
        // contents are never parsed or imported (and no 999 is produced).
        InterchangeIntakeResult? envelope = null;
        InterchangeIntakeRequest? intakeRequest = null;
        if (_interchangeIntake is not null)
        {
            intakeRequest = new InterchangeIntakeRequest
            {
                TenantId = tenantId,
                Content = ediContent,
                TransactionType = "834",
                FileName = file.FileName,
            };
            envelope = await _interchangeIntake.ReceiveAsync(intakeRequest, HttpContext?.RequestAborted ?? CancellationToken.None);

            if (envelope.IsRejected)
            {
                _logger.LogWarning("Uploaded 834 file {FileName} for tenant {TenantId} rejected at the interchange envelope",
                    LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId));
                return BadRequest(new
                {
                    error = "The interchange envelope (ISA/IEA) was rejected: " + string.Join("; ", envelope.RejectionReasons),
                    interchangeAcknowledgmentCode = Ta1AckCodes.Rejected,
                    acknowledgmentTa1 = envelope.Ta1,
                    ta1AcknowledgmentIds = envelope.AcknowledgmentIds,
                });
            }
            ediContent = envelope.AcceptedContent!;
        }

        // The envelope claimed its ISA13: a failure after it gives the number
        // back, so the partner's resend is not refused as a duplicate.
        try
        {
            var imported = await ImportAcceptedRaw834Async(file, tenantId, ediContent, envelope);
            if (imported.Result is ObjectResult { StatusCode: >= 400 } && envelope is not null && !_raw834ImportStarted)
                await _interchangeIntake!.ReleaseAsync(intakeRequest!, envelope, CancellationToken.None);
            return imported;
        }
        catch when (envelope is not null && !_raw834ImportStarted)
        {
            await _interchangeIntake!.ReleaseAsync(intakeRequest!, envelope, CancellationToken.None);
            throw;
        }
    }

    private async Task<ActionResult<ImportResult>> ImportAcceptedRaw834Async(
        IFormFile file, string tenantId, string ediContent, InterchangeIntakeResult? envelope)
    {
        Enrollment834 enrollment;
        try
        {
            enrollment = _ediParser.Parse(ediContent, file.FileName);
        }
        catch (X12FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded 834 file {FileName} for tenant {TenantId}",
                LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId));
            return BadRequest($"Could not parse 834 file: {ex.Message}");
        }

        _logger.LogInformation("Parsed uploaded 834 file {FileName} for tenant {TenantId} with {Count} enrollments",
            LogSanitizer.Sanitize(enrollment.FileName), LogSanitizer.Sanitize(tenantId), enrollment.TransactionCount);

        // The importing user is the token subject; nothing in the body can claim it.
        enrollment.ActorId = _actor.UserId;
        _raw834ImportStarted = true;
        var result = await _importService.ImportEnrollmentAsync(enrollment, tenantId);

        if (envelope is not null)
        {
            result.InterchangeAcknowledgmentCode = envelope.Interchanges.Any(i => i.IsRejected) ? Ta1AckCodes.Rejected
                : envelope.Interchanges.Any(i => i.Decision.AckCode == Ta1AckCodes.AcceptedWithErrors) ? Ta1AckCodes.AcceptedWithErrors
                : Ta1AckCodes.Accepted;
            result.AcknowledgmentTa1 = envelope.Ta1;
            result.Ta1AcknowledgmentIds = envelope.AcknowledgmentIds.ToList();
        }

        return Ok(result);
    }

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
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<PlanCodeGapReport>> PlanCodeGapReportRaw834(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        var tenantId = TenantId;

        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty 834 file is required.");
        }

        string ediContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            ediContent = await reader.ReadToEndAsync();
        }

        Enrollment834 enrollment;
        try
        {
            enrollment = _ediParser.Parse(ediContent, file.FileName);
        }
        catch (X12FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded 834 file {FileName} for tenant {TenantId}",
                LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId));
            return BadRequest($"Could not parse 834 file: {ex.Message}");
        }

        var report = await _gapReportService.BuildReportAsync(enrollment, tenantId, ct);
        return Ok(report);
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
