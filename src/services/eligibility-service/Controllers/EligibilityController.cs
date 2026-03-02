using Microsoft.AspNetCore.Mvc;
using EligibilityService.Middleware;
using EligibilityService.Models;
using EligibilityService.Services;

namespace EligibilityService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EligibilityController : ControllerBase
{
    private readonly IEligibilityService _eligibilityService;
    private readonly ILogger<EligibilityController> _logger;
    
    public string TenantId { get; set; } = string.Empty;

    public EligibilityController(
        IEligibilityService eligibilityService,
        ILogger<EligibilityController> logger)
    {
        _eligibilityService = eligibilityService;
        _logger = logger;
    }

    /// <summary>
    /// Submit 270 Eligibility Inquiry - Real-time eligibility check
    /// </summary>
    [HttpPost("inquiry")]
    public async Task<ActionResult<EligibilityResponse>> SubmitInquiry([FromBody] EligibilityInquiry inquiry)
    {
        try
        {
            inquiry.TenantId = TenantId;
            inquiry.ControlNumber = GenerateControlNumber();
            
            _logger.LogInformation("Processing eligibility inquiry for member {SubscriberId}", SanitizeForLog(inquiry.SubscriberId));
            
            var response = await _eligibilityService.ProcessInquiryAsync(inquiry);
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing eligibility inquiry");
            return StatusCode(500, new { error = "Error processing inquiry", details = ex.Message });
        }
    }

    /// <summary>
    /// Quick eligibility check - Returns active/inactive status only
    /// </summary>
    [HttpGet("check")]
    public async Task<ActionResult<QuickEligibilityResponse>> QuickCheck(
        [FromQuery] string subscriberId,
        [FromQuery] string? groupNumber = null,
        [FromQuery] DateTime? serviceDate = null)
    {
        try
        {
            var isEligible = await _eligibilityService.QuickEligibilityCheckAsync(
                TenantId, subscriberId, groupNumber, serviceDate ?? DateTime.Today);
            
            return Ok(new QuickEligibilityResponse
            {
                SubscriberId = subscriberId,
                IsEligible = isEligible.IsActive,
                StatusCode = isEligible.StatusCode,
                CoverageLevel = isEligible.CoverageLevel,
                Message = isEligible.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in quick eligibility check");
            return StatusCode(500, new { error = "Error checking eligibility" });
        }
    }

    /// <summary>
    /// Get benefit details for a member
    /// </summary>
    [HttpGet("benefits/{subscriberId}")]
    public async Task<ActionResult<List<EligibilityBenefit>>> GetBenefits(
        string subscriberId,
        [FromQuery] string? serviceType = null,
        [FromQuery] DateTime? serviceDate = null)
    {
        try
        {
            var benefits = await _eligibilityService.GetBenefitDetailsAsync(
                TenantId, subscriberId, serviceType, serviceDate ?? DateTime.Today);
            
            return Ok(benefits);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving benefits");
            return StatusCode(500, new { error = "Error retrieving benefits" });
        }
    }

    /// <summary>
    /// Get deductible and out-of-pocket information
    /// </summary>
    [HttpGet("accumulation/{subscriberId}")]
    public async Task<ActionResult<AccumulationResponse>> GetAccumulation(string subscriberId)
    {
        try
        {
            var accumulation = await _eligibilityService.GetAccumulationAsync(TenantId, subscriberId);
            
            return Ok(accumulation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving accumulation");
            return StatusCode(500, new { error = "Error retrieving accumulation" });
        }
    }

    /// <summary>
    /// Get eligibility inquiry history
    /// </summary>
    [HttpGet("history/{subscriberId}")]
    public async Task<ActionResult<List<EligibilityInquiry>>> GetHistory(
        string subscriberId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        try
        {
            var history = await _eligibilityService.GetInquiryHistoryAsync(
                TenantId, subscriberId, page, pageSize);
            
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving history");
            return StatusCode(500, new { error = "Error retrieving history" });
        }
    }

    /// <summary>
    /// Validate authorization requirement for a service
    /// </summary>
    [HttpPost("validate-auth")]
    public async Task<ActionResult<AuthRequirementResponse>> ValidateAuthRequirement(
        [FromBody] AuthRequirementRequest request)
    {
        try
        {
            var requiresAuth = await _eligibilityService.CheckAuthRequirementAsync(
                TenantId, request.SubscriberId, request.ServiceTypeCode, request.ProcedureCode);
            
            return Ok(new AuthRequirementResponse
            {
                RequiresAuth = requiresAuth.Required,
                Reason = requiresAuth.Reason,
                ServiceType = request.ServiceTypeCode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating auth requirement");
            return StatusCode(500, new { error = "Error validating authorization" });
        }
    }

    private string GenerateControlNumber()
    {
        return DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// DTOs
public class QuickEligibilityResponse
{
    public string SubscriberId { get; set; } = string.Empty;
    public bool IsEligible { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class AccumulationResponse
{
    public string SubscriberId { get; set; } = string.Empty;
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }
    public DateTime AsOfDate { get; set; } = DateTime.Today;
}

public class AuthRequirementRequest
{
    public string SubscriberId { get; set; } = string.Empty;
    public string ServiceTypeCode { get; set; } = string.Empty;
    public string? ProcedureCode { get; set; }
}

public class AuthRequirementResponse
{
    public bool RequiresAuth { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
}
