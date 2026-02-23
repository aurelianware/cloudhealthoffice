using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

[ApiController]
[Route("api/v1/enrollment")]
public class EnrollmentController : ControllerBase
{
    private readonly IEnrollmentImportService _importService;
    private readonly ILogger<EnrollmentController> _logger;
    
    public EnrollmentController(IEnrollmentImportService importService, ILogger<EnrollmentController> logger)
    {
        _importService = importService;
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
            enrollment.FileName, tenantId, enrollment.TransactionCount);
        
        var result = await _importService.ImportEnrollmentAsync(enrollment, tenantId);
        
        return Ok(result);
    }
    
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "enrollment-import" });
    }
}
