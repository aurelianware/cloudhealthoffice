using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Security;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.PricingApi.Controllers;

/// <summary>
/// Self-service signup for free-tier API keys.
///
/// No longer anonymous: anyone on the public ingress could mint unlimited keys
/// under any organization name, and the per-minute limiter does not bound it
/// (see README). No CHO surface calls it; the public site collects requests
/// through its lead-capture form and keys are issued by a platform admin.
/// Re-opening it anonymously is a product decision (with abuse controls).
/// </summary>
[ApiController]
[Route("api/v1/signup")]
[RequirePermission(PricingApiAuth.GlobalWritePermission)]
[Produces("application/json")]
public class SignupController : ControllerBase
{
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly ICurrentActor _actor;
    private readonly ILogger<SignupController> _logger;

    public SignupController(IApiKeyRepository apiKeyRepo, ICurrentActor actor, ILogger<SignupController> logger)
    {
        _apiKeyRepo = apiKeyRepo;
        _actor = actor;
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
            IsActive = true,
            CreatedBy = _actor.UserId
        };

        await _apiKeyRepo.CreateAsync(record);

        _logger.LogInformation("AUDIT pricing free-tier signup: {Org} ({Email}) by {Actor}",
            SanitizeForLog(record.TenantName), SanitizeForLog(record.ContactEmail), _actor.UserId);

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
