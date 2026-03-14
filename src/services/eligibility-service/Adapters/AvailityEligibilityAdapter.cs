using EligibilityService.Models;

namespace EligibilityService.Adapters;

/// <summary>
/// Eligibility adapter for the Availity platform.
/// Calls Availity's 270/271 API to verify member eligibility.
///
/// Configuration via EligibilityConfig.PlatformSettings:
///   - "availity:payerId" — Availity payer ID for routing
///   - "availity:submitterId" — Submitter ID for the request
///
/// Credentials are stored in Azure Key Vault (EligibilityConfig.KeyVaultSecretName).
/// </summary>
public class AvailityEligibilityAdapter : IEligibilityAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AvailityEligibilityAdapter> _logger;

    public string Platform => "availity";

    public AvailityEligibilityAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<AvailityEligibilityAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
        EligibilityAdapterRequest request, CancellationToken ct = default)
    {
        // TODO: Implement Availity 270/271 API integration
        // 1. Build Availity-specific request from normalized EligibilityAdapterRequest
        // 2. Authenticate using credentials from Key Vault
        // 3. POST to Availity eligibility endpoint (request.PlatformSettings["availity:apiEndpoint"])
        // 4. Parse Availity response into normalized EligibilityAdapterResponse
        // 5. Store raw response for audit trail

        _logger.LogWarning("Availity eligibility adapter is not yet implemented");

        throw new NotImplementedException(
            "Availity eligibility adapter is not yet implemented. " +
            "Configure tenant with platform='cho' to use the default CHO eligibility service.");
    }
}
