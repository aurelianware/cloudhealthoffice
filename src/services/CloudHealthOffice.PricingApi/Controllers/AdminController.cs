using System.Security.Cryptography;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
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

    public AdminController(
        IApiKeyRepository apiKeyRepo,
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<AdminController> logger)
    {
        _apiKeyRepo = apiKeyRepo;
        _apiKeyCollection = database.GetCollection<ApiKeyRecord>("api_keys");
        _configuration = configuration;
        _logger = logger;
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
