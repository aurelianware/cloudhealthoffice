namespace ProviderEligibilityApi.Security;

/// <summary>
/// Provider applications (for example CloudDentalOffice) allowed to call this
/// API with the <see cref="ProviderApiKeyAuthenticationHandler.SchemeName"/>
/// scheme. Each credential is bound to exactly one tenant: every request it
/// authenticates acts for that tenant, never for one the request names. A
/// provider application acting for several tenants holds one credential per
/// tenant.
/// </summary>
public sealed class ProviderApiOptions
{
    public const string SectionName = "ProviderApi";

    public List<ProviderApiClient> Clients { get; set; } = new();
}

public sealed class ProviderApiClient
{
    /// <summary>Non-secret client label used in logs and metrics.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Shared service credential sent in <c>X-Api-Key</c>. Supply from Key Vault.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The one tenant this credential acts for. A client without one is not
    /// usable (fail closed). The former <c>Tenants</c> allow-list, from which a
    /// request picked its tenant with <c>X-Tenant-ID</c>, is no longer read.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    internal bool IsUsable =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(TenantId);
}
