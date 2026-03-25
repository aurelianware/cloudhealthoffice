using System.Security.Cryptography;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.PricingApi.Controllers;

/// <summary>
/// Public self-service signup for free-tier API keys.
/// No authentication required — rate-limited by IP via the global rate limiter.
/// </summary>
[ApiController]
[Route("api/v1/signup")]
[Produces("application/json")]
public class SignupController : ControllerBase
{
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly ILogger<SignupController> _logger;

    public SignupController(IApiKeyRepository apiKeyRepo, ILogger<SignupController> logger)
    {
        _apiKeyRepo = apiKeyRepo;
        _logger = logger;
    }

    /// <summary>Sign up for a free API key.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SignupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationName))
            return BadRequest(new { error = "Organization name is required." });

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return BadRequest(new { error = "A valid email address is required." });

        var apiKey = "cho_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var record = new ApiKeyRecord
        {
            ApiKey = apiKey,
            TenantName = request.OrganizationName.Trim(),
            ContactEmail = request.Email.Trim().ToLowerInvariant(),
            Tier = PricingTier.Free,
            MonthlyLimit = 1_000,
            CurrentMonthUsage = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        await _apiKeyRepo.CreateAsync(record);

        _logger.LogInformation("New free-tier signup: {Org} ({Email})",
            SanitizeForLog(record.TenantName), SanitizeForLog(record.ContactEmail));

        return StatusCode(StatusCodes.Status201Created, new SignupResponse
        {
            ApiKey = apiKey,
            Tier = "Free",
            MonthlyLimit = 1_000,
            Message = "Your API key has been created. Include it in the X-API-Key header with every request."
        });
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}

public record SignupRequest
{
    public string OrganizationName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

public record SignupResponse
{
    public string ApiKey { get; init; } = string.Empty;
    public string Tier { get; init; } = string.Empty;
    public int MonthlyLimit { get; init; }
    public string Message { get; init; } = string.Empty;
}
