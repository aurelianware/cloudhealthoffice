using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Security;
using CloudHealthOffice.PricingApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.PricingApi.Controllers;

/// <summary>
/// Admin API for managing API keys, usage and the fee schedules.
/// POST/GET/DELETE /api/v1/admin/api-keys
///
/// Platform only: the API keys belong to every external customer and the fee
/// schedules are global (CMS Medicare data every caller prices against), so
/// nothing here belongs to a CHO tenant. Every action needs platform:admin,
/// which tenant roles (even through *:*), service tokens and API-key customers
/// never hold. The acting admin comes from the CHO token and is recorded.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[RequirePermission(PricingApiAuth.GlobalWritePermission)]
[Produces("application/json")]
public class AdminController : ControllerBase
{
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly ICurrentActor _actor;
    private readonly ILogger<AdminController> _logger;
    private readonly IFeeScheduleLoaderService _feeScheduleLoader;

    public AdminController(
        IApiKeyRepository apiKeyRepo,
        ICurrentActor actor,
        ILogger<AdminController> logger,
        IFeeScheduleLoaderService feeScheduleLoader)
    {
        _apiKeyRepo = apiKeyRepo;
        _actor = actor;
        _logger = logger;
        _feeScheduleLoader = feeScheduleLoader;
    }

    /// <summary>
    /// Create a new API key for a tenant. The key is in this response only:
    /// the service stores its SHA-256 and prefix, never the key.
    /// </summary>
    [HttpPost("api-keys")]
    [ProducesResponseType(typeof(ApiResponse<ApiKeyCreated>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "INVALID_REQUEST", Message = "tenantName is required." }
            });
        }

        var monthlyLimit = PricingApiAuth.MonthlyLimit(request.Tier);

        var (apiKey, record) = ApiKeyHashing.Issue(
            request.TenantName, request.ContactEmail, request.Tier, monthlyLimit, _actor.UserId);

        var created = await _apiKeyRepo.CreateAsync(record);

        _logger.LogInformation("AUDIT pricing api-key {KeyId} ({KeyPrefix}) created for customer {Customer} (tier={Tier}) by {Actor}",
            created.KeyId, created.KeyPrefix, SanitizeForLog(request.TenantName), request.Tier, _actor.UserId);

        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ApiKeyCreated>
        {
            Data = new ApiKeyCreated { ApiKey = apiKey, Key = ApiKeyView.From(created) }
        });
    }

    /// <summary>
    /// List all API keys: key id, prefix and metadata. Neither the key nor its
    /// hash is ever returned (the service no longer has the key).
    /// </summary>
    [HttpGet("api-keys")]
    [ProducesResponseType(typeof(ApiResponse<List<ApiKeyView>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListApiKeys()
    {
        var keys = await _apiKeyRepo.ListAsync();
        return Ok(new ApiResponse<List<ApiKeyView>> { Data = keys.Select(ApiKeyView.From).ToList() });
    }

    /// <summary>
    /// Deactivate an API key (sets IsActive to false), addressed by its key id
    /// (<c>pk_…</c> from the create or list response). The key itself is never
    /// accepted in a URL: URLs end up in access logs and proxies.
    /// </summary>
    [HttpDelete("api-keys/{keyId}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeactivateApiKey([FromRoute] string keyId)
    {
        var existing = await _apiKeyRepo.GetByIdAsync(keyId);
        if (existing is null)
        {
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "NOT_FOUND", Message = "API key not found. Address a key by its key id." }
            });
        }

        await _apiKeyRepo.DeactivateAsync(existing.KeyId, _actor.UserId, DateTimeOffset.UtcNow);

        _logger.LogInformation("AUDIT pricing api-key {KeyId} ({KeyPrefix}) deactivated for customer {Customer} by {Actor}",
            existing.KeyId, existing.KeyPrefix, SanitizeForLog(existing.TenantName), _actor.UserId);

        return Ok(new ApiResponse<object> { Data = new { message = "API key deactivated.", keyId = existing.KeyId, tenantName = existing.TenantName } });
    }

    /// <summary>
    /// Reset monthly usage counters for all API keys.
    /// </summary>
    [HttpPost("api-keys/reset-usage")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResetUsage()
    {
        await _apiKeyRepo.ResetMonthlyUsageAsync();

        _logger.LogInformation("AUDIT pricing api-key usage reset for all keys by {Actor}", _actor.UserId);

        return Ok(new ApiResponse<object> { Data = new { message = "Monthly usage reset for all API keys." } });
    }

    // ─────────────────────────────────────────────────────────────
    //  Fee Schedule Upload Endpoints
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Upload a CMS Physician Fee Schedule (RBRVS/PFSRVF) CSV file.
    /// </summary>
    [HttpPost("fee-schedules/upload/rbrvs")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UploadRbrvs(IFormFile file, [FromQuery] int year = 2025)
    {
        var validationResult = ValidateCsvFile(file);
        if (validationResult is not null) return validationResult;

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var codeCount = await _feeScheduleLoader.SeedMedicareRbrvs(tempPath, year);
            _logger.LogInformation("AUDIT pricing global fee schedule RBRVS {Year} loaded: {Count} codes by {Actor}", year, codeCount, _actor.UserId);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"RBRVS {year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_RBRVS_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import RBRVS {Year} (trace id {TraceId})", year, HttpContext.TraceIdentifier);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = ImportFailedMessage("RBRVS") }
            });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Upload a CMS OPPS Addendum B CSV file.
    /// </summary>
    [HttpPost("fee-schedules/upload/opps")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UploadOpps(IFormFile file, [FromQuery] int year = 2025)
    {
        var validationResult = ValidateCsvFile(file);
        if (validationResult is not null) return validationResult;

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var codeCount = await _feeScheduleLoader.SeedMedicareOpps(tempPath, year);
            _logger.LogInformation("AUDIT pricing global fee schedule OPPS {Year} loaded: {Count} codes by {Actor}", year, codeCount, _actor.UserId);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"OPPS {year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_OPPS_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import OPPS {Year} (trace id {TraceId})", year, HttpContext.TraceIdentifier);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = ImportFailedMessage("OPPS") }
            });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Upload a CMS MS-DRG Table 5 CSV file.
    /// </summary>
    [HttpPost("fee-schedules/upload/drg")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UploadDrg(IFormFile file, [FromQuery] int year = 2025, [FromQuery] decimal baseRate = 6377.73m)
    {
        var validationResult = ValidateCsvFile(file);
        if (validationResult is not null) return validationResult;

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var codeCount = await _feeScheduleLoader.SeedMedicareDrg(tempPath, year, baseRate);
            _logger.LogInformation("AUDIT pricing global fee schedule DRG {Year} loaded: {Count} codes (baseRate={BaseRate}) by {Actor}", year, codeCount, baseRate, _actor.UserId);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"MS-DRG FY{year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_DRG_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import DRG {Year} (trace id {TraceId})", year, HttpContext.TraceIdentifier);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = ImportFailedMessage("DRG") }
            });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Re-seed demo fee schedule data (resets to demo environment).
    /// </summary>
    [HttpPost("fee-schedules/seed-demo")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SeedDemoData()
    {
        await _feeScheduleLoader.SeedDemoDataAsync();

        _logger.LogInformation("AUDIT pricing global demo fee schedules re-seeded by {Actor}", _actor.UserId);

        return Ok(new ApiResponse<object>
        {
            Data = new { message = "Demo fee schedule data seeded successfully." }
        });
    }

    /// <summary>
    /// Validates that the uploaded file exists and has a .csv extension.
    /// </summary>
    private IActionResult? ValidateCsvFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "NO_FILE", Message = "A CSV file is required." }
            });
        }

        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "INVALID_FILE_TYPE", Message = "File must have a .csv extension." }
            });
        }

        return null;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    /// <summary>
    /// What the caller sees of a failed import. The exception (which can name
    /// temp paths, connection details or driver internals) is only logged,
    /// under the request's trace id.
    /// </summary>
    private string ImportFailedMessage(string kind) =>
        $"Failed to import {kind} CSV. Check that the file is the CMS layout; details are in the service log (trace id {HttpContext.TraceIdentifier}).";
}

/// <summary>
/// Request body for creating a new API key.
/// </summary>
public record CreateApiKeyRequest
{
    // The issuing admin is the token subject (CreatedBy); no actor field is read from the body.
    public required string TenantName { get; init; }
    public string? ContactEmail { get; init; }
    public PricingTier Tier { get; init; } = PricingTier.Free;
}
