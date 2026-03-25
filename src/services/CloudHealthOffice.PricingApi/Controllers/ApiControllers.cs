using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.PricingApi.Controllers;

/// <summary>
/// Claims repricing — the core value endpoint.
/// POST /api/v1/reprice
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public class RepricingController : ControllerBase
{
    private readonly IRepricingService _repricingService;
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly IUsageRepository _usageRepo;
    private readonly ILogger<RepricingController> _logger;

    public RepricingController(
        IRepricingService repricingService,
        IApiKeyRepository apiKeyRepo,
        IUsageRepository usageRepo,
        ILogger<RepricingController> logger)
    {
        _repricingService = repricingService;
        _apiKeyRepo = apiKeyRepo;
        _usageRepo = usageRepo;
        _logger = logger;
    }

    /// <summary>
    /// Reprice a claim against a Medicare or custom fee schedule.
    /// Returns allowed amounts with full pricing breakdown per line.
    /// </summary>
    /// <remarks>
    /// Supports Professional (RBRVS), Outpatient (OPPS), and Inpatient (MS-DRG) claim types.
    /// Multiple procedure reduction, modifier adjustments, and facility/non-facility differentials 
    /// are applied automatically per CMS rules.
    /// </remarks>
    [HttpPost("reprice")]
    [ProducesResponseType(typeof(ApiResponse<RepricingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Reprice([FromBody] RepricingRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (request.Lines is null or { Count: 0 })
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "INVALID_REQUEST", Message = "At least one claim line is required." }
            });
        }

        try
        {
            var result = await _repricingService.RepriceClaimAsync(request);
            sw.Stop();

            // Track usage
            await TrackUsageAsync("reprice", request.Lines.Count, (int)sw.ElapsedMilliseconds, true);

            return Ok(new ApiResponse<RepricingResponse> { Data = result });
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            await TrackUsageAsync("reprice", request.Lines.Count, (int)sw.ElapsedMilliseconds, false);

            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "PRICING_ERROR", Message = ex.Message }
            });
        }
    }

    /// <summary>
    /// Batch reprice multiple claims in a single request.
    /// </summary>
    [HttpPost("reprice/batch")]
    [ProducesResponseType(typeof(ApiResponse<List<RepricingResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RepriceBatch([FromBody] List<RepricingRequest> requests)
    {
        if (requests is null or { Count: 0 })
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "INVALID_REQUEST", Message = "At least one claim is required." }
            });
        }

        if (requests.Count > 100)
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "BATCH_TOO_LARGE", Message = "Maximum 100 claims per batch request." }
            });
        }

        var results = new List<RepricingResponse>();
        var totalLines = 0;

        foreach (var request in requests)
        {
            var result = await _repricingService.RepriceClaimAsync(request);
            results.Add(result);
            totalLines += request.Lines.Count;
        }

        await TrackUsageAsync("reprice/batch", totalLines, 0, true);

        return Ok(new ApiResponse<List<RepricingResponse>> { Data = results });
    }

    private async Task TrackUsageAsync(string endpoint, int lineCount, int responseTimeMs, bool success)
    {
        if (HttpContext.Items.TryGetValue("ApiKeyRecord", out var keyObj) && keyObj is ApiKeyRecord apiKey)
        {
            await _apiKeyRepo.IncrementUsageAsync(apiKey.ApiKey, lineCount);
            await _usageRepo.RecordUsageAsync(new UsageRecord
            {
                ApiKey = apiKey.ApiKey,
                Endpoint = endpoint,
                LineCount = lineCount,
                Timestamp = DateTimeOffset.UtcNow,
                ResponseTimeMs = responseTimeMs,
                Success = success
            });
        }
    }
}

/// <summary>
/// Single-code lookup — the "hello world" endpoint for exploring fee schedules.
/// GET /api/v1/lookup/{code}
/// </summary>
[ApiController]
[Route("api/v1/lookup")]
[Produces("application/json")]
public class LookupController : ControllerBase
{
    private readonly IRepricingService _repricingService;

    public LookupController(IRepricingService repricingService)
    {
        _repricingService = repricingService;
    }

    /// <summary>
    /// Look up the allowed amount and RVU components for a single procedure code.
    /// Great for spot-checking rates or building fee schedule comparison tools.
    /// </summary>
    /// <param name="code">CPT/HCPCS procedure code (e.g., 99213, 27447)</param>
    /// <param name="feeScheduleId">Fee schedule identifier (e.g., MEDICARE_RBRVS_2025)</param>
    /// <param name="locality">Medicare locality code for geographic adjustment</param>
    /// <param name="facility">If true, return facility rate; otherwise non-facility</param>
    [HttpGet("{code}")]
    [ProducesResponseType(typeof(ApiResponse<CodeLookupResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Lookup(
        [FromRoute] string code,
        [FromQuery] string feeScheduleId = "MEDICARE_RBRVS_2025",
        [FromQuery] string? locality = null,
        [FromQuery] bool facility = false)
    {
        var result = await _repricingService.LookupCodeAsync(new CodeLookupRequest
        {
            ProcedureCode = code.Trim(),
            FeeScheduleId = feeScheduleId,
            Locality = locality,
            Facility = facility
        });

        if (result is null)
        {
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError
                {
                    Code = "CODE_NOT_FOUND",
                    Message = $"Procedure code '{code}' not found in fee schedule '{feeScheduleId}'."
                }
            });
        }

        return Ok(new ApiResponse<CodeLookupResponse> { Data = result });
    }
}

/// <summary>
/// Fee schedule catalog — browse available fee schedules.
/// GET /api/v1/fee-schedules  (no API key required)
/// </summary>
[ApiController]
[Route("api/v1/fee-schedules")]
[Produces("application/json")]
public class FeeScheduleController : ControllerBase
{
    private readonly IFeeScheduleRepository _feeScheduleRepo;

    public FeeScheduleController(IFeeScheduleRepository feeScheduleRepo)
    {
        _feeScheduleRepo = feeScheduleRepo;
    }

    /// <summary>
    /// List all available fee schedules with metadata.
    /// No API key required — browse before you sign up.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<FeeScheduleInfo>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSchedules()
    {
        var schedules = await _feeScheduleRepo.GetAllSchedulesAsync();
        return Ok(new ApiResponse<List<FeeScheduleInfo>> { Data = schedules });
    }

    /// <summary>
    /// Get details for a specific fee schedule.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<FeeScheduleInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchedule([FromRoute] string id)
    {
        var schedule = await _feeScheduleRepo.GetScheduleInfoAsync(id);
        if (schedule is null)
        {
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "NOT_FOUND", Message = $"Fee schedule '{id}' not found." }
            });
        }

        return Ok(new ApiResponse<FeeScheduleInfo> { Data = schedule });
    }
}
