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
    private readonly ILogger<EnrollmentController> _logger;

    public EnrollmentController(
        IEnrollmentImportService importService,
        IEnrollment834EdiParser ediParser,
        ILogger<EnrollmentController> logger)
    {
        _importService = importService;
        _ediParser = ediParser;
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
