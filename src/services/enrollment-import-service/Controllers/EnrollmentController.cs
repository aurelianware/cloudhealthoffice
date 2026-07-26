using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

[ApiController]
[Route("api/v1/enrollment")]
public class EnrollmentController : ControllerBase
{
    private readonly IEnrollmentImportService _importService;
    private readonly IEnrollment834EdiParser _ediParser;
    private readonly IPlanCodeGapReportService _gapReportService;
    private readonly IEnrollmentImportRunRepository _importRuns;
    private readonly ILogger<EnrollmentController> _logger;

    public EnrollmentController(
        IEnrollmentImportService importService,
        IEnrollment834EdiParser ediParser,
        IPlanCodeGapReportService gapReportService,
        IEnrollmentImportRunRepository importRuns,
        ILogger<EnrollmentController> logger)
    {
        _importService = importService;
        _ediParser = ediParser;
        _gapReportService = gapReportService;
        _importRuns = importRuns;
        _logger = logger;
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportResult>> ImportEnrollment(
        [FromBody] Enrollment834 enrollment,
        [FromHeader(Name = "X-Tenant-ID")] string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("X-Tenant-ID header is required");
        }

        _logger.LogInformation("Importing 834 file {FileName} for tenant {TenantId} with {Count} enrollments",
            SanitizeForLog(enrollment.FileName), SanitizeForLog(tenantId), enrollment.TransactionCount);

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
        [FromForm] IFormFile file,
        [FromHeader(Name = "X-Tenant-ID")] string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("X-Tenant-ID header is required");
        }

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
                SanitizeForLog(file.FileName), SanitizeForLog(tenantId));
            return BadRequest($"Could not parse 834 file: {ex.Message}");
        }

        _logger.LogInformation("Parsed uploaded 834 file {FileName} for tenant {TenantId} with {Count} enrollments",
            SanitizeForLog(enrollment.FileName), SanitizeForLog(tenantId), enrollment.TransactionCount);

        var result = await _importService.ImportEnrollmentAsync(enrollment, tenantId);

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
    public async Task<ActionResult<PlanCodeGapReport>> PlanCodeGapReport(
        [FromBody] Enrollment834 enrollment,
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("X-Tenant-ID header is required");
        }

        var report = await _gapReportService.BuildReportAsync(enrollment, tenantId, ct);
        return Ok(report);
    }

    /// <summary>Same as <see cref="PlanCodeGapReport"/>, but for a raw X12 834 file upload.</summary>
    [HttpPost("plan-code-gap-report/raw834")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<PlanCodeGapReport>> PlanCodeGapReportRaw834(
        [FromForm] IFormFile file,
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("X-Tenant-ID header is required");
        }

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
                SanitizeForLog(file.FileName), SanitizeForLog(tenantId));
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
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        [FromQuery] int limit = 100)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("X-Tenant-ID header is required");
        }
        if (limit < 1 || limit > 500) limit = 100;

        var runs = await _importRuns.ListRecentAsync(tenantId, limit);
        return Ok(runs);
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "enrollment-import" });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
