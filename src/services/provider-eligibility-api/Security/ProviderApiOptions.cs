namespace ProviderEligibilityApi.Security;

/// <summary>
/// Provider applications (for example CloudDentalOffice) allowed to call this
/// API. Each client has its own credential and an explicit tenant allow-list,
/// so a credential issued for one practice can never act for another.
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

    /// <summary>Tenant ids this client may act for. Empty means no tenant.</summary>
    public List<string> Tenants { get; set; } = new();

    internal bool IsUsable =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        Tenants.Any(t => !string.IsNullOrWhiteSpace(t));
}
