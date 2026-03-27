using System.Security.Cryptography;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace CloudHealthOffice.PricingApi.Controllers;

/// <summary>
/// Admin API for managing API keys and usage.
/// Protected by X-Admin-Secret header.
/// POST/GET/DELETE /api/v1/admin/api-keys
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
public class AdminController : ControllerBase
{
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly IMongoCollection<ApiKeyRecord> _apiKeyCollection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminController> _logger;
    private readonly IFeeScheduleLoaderService _feeScheduleLoader;

    public AdminController(
        IApiKeyRepository apiKeyRepo,
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<AdminController> logger,
        IFeeScheduleLoaderService feeScheduleLoader)
    {
        _apiKeyRepo = apiKeyRepo;
        _apiKeyCollection = database.GetCollection<ApiKeyRecord>("api_keys");
        _configuration = configuration;
        _logger = logger;
        _feeScheduleLoader = feeScheduleLoader;
    }

    /// <summary>
    /// Create a new API key for a tenant.
    /// </summary>
    [HttpPost("api-keys")]
    [ProducesResponseType(typeof(ApiResponse<ApiKeyRecord>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest request)
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "INVALID_REQUEST", Message = "tenantName is required." }
            });
        }

        var monthlyLimit = request.Tier switch
        {
            PricingTier.Free => 1_000,
            PricingTier.Starter => 10_000,
            PricingTier.Professional => 100_000,
            PricingTier.Enterprise => int.MaxValue,
            _ => 1_000
        };

        var apiKey = "cho_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var record = new ApiKeyRecord
        {
            ApiKey = apiKey,
            TenantName = request.TenantName,
            ContactEmail = request.ContactEmail,
            Tier = request.Tier,
            MonthlyLimit = monthlyLimit,
            CurrentMonthUsage = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        var created = await _apiKeyRepo.CreateAsync(record);

        _logger.LogInformation("Admin created API key for tenant {Tenant} (tier={Tier})", request.TenantName, request.Tier);

        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ApiKeyRecord> { Data = created });
    }

    /// <summary>
    /// List all API keys. Keys are redacted to show only the first 8 characters.
    /// </summary>
    [HttpGet("api-keys")]
    [ProducesResponseType(typeof(ApiResponse<List<ApiKeyRecord>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListApiKeys()
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

        var keys = await _apiKeyCollection.Find(_ => true).ToListAsync();

        // Redact keys — show only first 8 chars
        var redacted = keys.Select(k => k with { ApiKey = k.ApiKey[..Math.Min(8, k.ApiKey.Length)] + "..." }).ToList();

        return Ok(new ApiResponse<List<ApiKeyRecord>> { Data = redacted });
    }

    /// <summary>
    /// Deactivate an API key (sets IsActive to false).
    /// </summary>
    [HttpDelete("api-keys/{apiKey}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeactivateApiKey([FromRoute] string apiKey)
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

        var existing = await _apiKeyRepo.GetByKeyAsync(apiKey);
        if (existing is null)
        {
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "NOT_FOUND", Message = $"API key not found." }
            });
        }

        var update = Builders<ApiKeyRecord>.Update.Set(k => k.IsActive, false);
        await _apiKeyCollection.UpdateOneAsync(k => k.ApiKey == apiKey, update);

        _logger.LogInformation("Admin deactivated API key for tenant {Tenant}", existing.TenantName);

        return Ok(new ApiResponse<object> { Data = new { message = "API key deactivated.", tenantName = existing.TenantName } });
    }

    /// <summary>
    /// Reset monthly usage counters for all API keys.
    /// </summary>
    [HttpPost("api-keys/reset-usage")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResetUsage()
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

        await _apiKeyRepo.ResetMonthlyUsageAsync();

        _logger.LogInformation("Admin reset monthly usage for all API keys");

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
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UploadRbrvs(IFormFile file, [FromQuery] int year = 2025)
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

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
            _logger.LogInformation("Admin uploaded RBRVS {Year}: {Count} codes", year, codeCount);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"RBRVS {year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_RBRVS_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import RBRVS {Year}", year);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = $"Failed to import RBRVS CSV: {ex.Message}" }
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
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UploadOpps(IFormFile file, [FromQuery] int year = 2025)
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

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
            _logger.LogInformation("Admin uploaded OPPS {Year}: {Count} codes", year, codeCount);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"OPPS {year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_OPPS_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import OPPS {Year}", year);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = $"Failed to import OPPS CSV: {ex.Message}" }
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
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UploadDrg(IFormFile file, [FromQuery] int year = 2025, [FromQuery] decimal baseRate = 6377.73m)
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

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
            _logger.LogInformation("Admin uploaded DRG {Year}: {Count} codes (baseRate={BaseRate})", year, codeCount, baseRate);

            return Ok(new ApiResponse<object>
            {
                Data = new { message = $"MS-DRG FY{year} imported successfully.", codeCount, feeScheduleId = $"MEDICARE_DRG_{year}" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import DRG {Year}", year);
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "IMPORT_FAILED", Message = $"Failed to import DRG CSV: {ex.Message}" }
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
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SeedDemoData()
    {
        var authResult = ValidateAdminSecret();
        if (authResult is not null) return authResult;

        await _feeScheduleLoader.SeedDemoDataAsync();

        _logger.LogInformation("Admin re-seeded demo fee schedule data");

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

    /// <summary>
    /// Validates the X-Admin-Secret header against the configured admin secret.
    /// Returns null if valid, or an IActionResult to short-circuit if invalid.
    /// </summary>
    private IActionResult? ValidateAdminSecret()
    {
        var configuredSecret = _configuration.GetValue<string>("PricingApi:AdminSecret") ?? "";

        if (string.IsNullOrEmpty(configuredSecret))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "ADMIN_NOT_CONFIGURED", Message = "Admin API not configured." }
            });
        }

        if (!Request.Headers.TryGetValue("X-Admin-Secret", out var providedSecret) ||
            providedSecret.ToString() != configuredSecret)
        {
            return Unauthorized(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError { Code = "UNAUTHORIZED", Message = "Invalid or missing admin secret." }
            });
        }

        return null;
    }
}

/// <summary>
/// Request body for creating a new API key.
/// </summary>
public record CreateApiKeyRequest
{
    public required string TenantName { get; init; }
    public string? ContactEmail { get; init; }
    public PricingTier Tier { get; init; } = PricingTier.Free;
}
