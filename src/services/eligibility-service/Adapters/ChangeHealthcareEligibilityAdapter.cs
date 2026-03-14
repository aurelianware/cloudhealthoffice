using EligibilityService.Models;

namespace EligibilityService.Adapters;

/// <summary>
/// Eligibility adapter for the Change Healthcare (Optum) platform.
/// Calls Change Healthcare's eligibility API to verify member coverage.
///
/// Configuration via EligibilityConfig.PlatformSettings:
///   - "chc:tradingPartnerId" — Change Healthcare trading partner ID
///   - "chc:submitterOrgName" — Submitter organization name
///
/// Credentials are stored in Azure Key Vault (EligibilityConfig.KeyVaultSecretName).
/// </summary>
public class ChangeHealthcareEligibilityAdapter : IEligibilityAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChangeHealthcareEligibilityAdapter> _logger;

    public string Platform => "change-healthcare";

    public ChangeHealthcareEligibilityAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<ChangeHealthcareEligibilityAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
        EligibilityAdapterRequest request, CancellationToken ct = default)
    {
        // TODO: Implement Change Healthcare eligibility API integration
        // 1. Build CHC-specific request from normalized EligibilityAdapterRequest
        // 2. Authenticate using OAuth credentials from Key Vault
        // 3. POST to Change Healthcare eligibility endpoint
        // 4. Parse CHC response into normalized EligibilityAdapterResponse
        // 5. Store raw response for audit trail

        _logger.LogWarning("Change Healthcare eligibility adapter is not yet implemented");

        throw new NotImplementedException(
            "Change Healthcare eligibility adapter is not yet implemented. " +
            "Configure tenant with platform='cho' to use the default CHO eligibility service.");
    }
}
