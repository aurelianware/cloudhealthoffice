using Microsoft.AspNetCore.Mvc;
using EligibilityService.Middleware;
using EligibilityService.Models;
using EligibilityService.Repositories;
using EligibilityService.Services;

namespace EligibilityService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EligibilityController : ControllerBase
{
    private readonly IEligibilityService _eligibilityService;
    private readonly IEligibilityRepository _repository;
    private readonly IEdi270Parser _edi270Parser;
    private readonly IEdi271Generator _edi271Generator;
    private readonly ILogger<EligibilityController> _logger;

    public string TenantId { get; set; } = string.Empty;

    public EligibilityController(
        IEligibilityService eligibilityService,
        IEligibilityRepository repository,
        IEdi270Parser edi270Parser,
        IEdi271Generator edi271Generator,
        ILogger<EligibilityController> logger)
    {
        _eligibilityService = eligibilityService;
        _repository = repository;
        _edi270Parser = edi270Parser;
        _edi271Generator = edi271Generator;
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

    /// <summary>
    /// X12 270/271 EDI endpoint — accepts raw 270 text, returns raw 271 text.
    /// Content-Type: text/plain; body = raw X12 270 EDI string.
    /// </summary>
    [HttpPost("270")]
    [Consumes("text/plain")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessEdi270()
    {
        string edi270;
        using (var reader = new System.IO.StreamReader(Request.Body))
        {
            edi270 = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(edi270))
            return BadRequest("Request body must contain the raw X12 270 EDI string.");

        Edi270ParseResult parsed;
        try
        {
            parsed = _edi270Parser.Parse(edi270);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse 270 EDI");
            return BadRequest($"Invalid 270 EDI: {ex.Message}");
        }

        var inquiry = parsed.Inquiry;
        inquiry.TenantId      = TenantId;
        inquiry.ControlNumber = string.IsNullOrEmpty(inquiry.ControlNumber)
            ? GenerateControlNumber()
            : inquiry.ControlNumber;

        _logger.LogInformation(
            "Processing EDI 270 for subscriber {SubscriberId}, serviceType={ServiceType}",
            SanitizeForLog(inquiry.SubscriberId), inquiry.ServiceTypeCode);

        EligibilityResponse response;
        try
        {
            response = await _eligibilityService.ProcessInquiryAsync(inquiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing eligibility inquiry from 270 EDI");
            return StatusCode(500, "Error processing eligibility inquiry.");
        }

        // Swap ISA sender/receiver so the 271 flows back to the submitter
        var edi271 = _edi271Generator.Generate(
            inquiry, response,
            isaSenderId:   parsed.InterchangeReceiverId, // 270's receiver = 271's sender (payer)
            isaReceiverId: parsed.InterchangeSenderId);  // 270's sender  = 271's receiver (provider)

        Response.Headers["Content-Disposition"] =
            $"inline; filename=\"271_{inquiry.SubscriberId}_{DateTime.UtcNow:yyyyMMdd}.edi\"";
        return Content(edi271, "text/plain");
    }

    /// <summary>
    /// Download X12 271 EDI for a stored eligibility inquiry.
    /// </summary>
    [HttpGet("{id}/271")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEdi271(string id)
    {
        var inquiry = await _repository.GetInquiryByIdAsync(TenantId, id);
        if (inquiry == null)
            return NotFound($"Eligibility inquiry {id} not found.");

        if (string.IsNullOrEmpty(inquiry.ResponseId))
            return NotFound($"No 271 response yet for inquiry {id}.");

        var response = await _repository.GetResponseByInquiryIdAsync(TenantId, id);
        if (response == null)
            return NotFound($"271 response not found for inquiry {id}.");

        _logger.LogInformation(
            "Generating 271 EDI for inquiry {InquiryId} (subscriber={SubscriberId})",
            SanitizeForLog(id), SanitizeForLog(inquiry.SubscriberId));

        var edi271 = _edi271Generator.Generate(
            inquiry, response,
            isaSenderId:   inquiry.PayerId,   // payer is the 271 sender
            isaReceiverId: inquiry.ProviderId);

        var filename = $"271_{inquiry.SubscriberId}_{inquiry.CreatedDate:yyyyMMdd}.edi";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        return Content(edi271, "text/plain");
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
