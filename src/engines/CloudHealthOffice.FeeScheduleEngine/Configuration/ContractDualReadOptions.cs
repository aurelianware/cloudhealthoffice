using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.FeeScheduleEngine.Configuration;

/// <summary>
/// Which tenants are inside the dual-read window of ADR 016 phase 4.
///
/// <para>
/// The window is per tenant and time-boxed: a tenant enters it, its claims are
/// priced twice for one full monthly cycle, and it leaves once the parity metric
/// shows nothing but matches. It costs an extra pricing pass per claim, so it is
/// off for everyone until a tenant is named.
/// </para>
///
/// <code>
/// FeeScheduleEngine:ContractDualRead:Enabled   false by default
/// FeeScheduleEngine:ContractDualRead:Tenants   comma-separated; empty means every tenant
/// FeeScheduleEngine:CanonicalProviderContractCollection   default ProviderContractsCanonical
/// </code>
/// </summary>
public sealed class ContractDualReadOptions
{
    public const string DefaultCanonicalCollection = "ProviderContractsCanonical";

    /// <summary>Off until a deployment turns it on. Nothing shadows while it is false.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The tenants in the window. Empty means every tenant, which is what a
    /// single-tenant deployment wants and what a wide window looks like at the
    /// end of the rollout.
    /// </summary>
    public IReadOnlySet<string> Tenants { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string CanonicalContractCollection { get; init; } = DefaultCanonicalCollection;

    public bool IsInWindow(string tenantId)
        => Enabled && (Tenants.Count == 0 || Tenants.Contains(tenantId));

    public static ContractDualReadOptions FromConfiguration(IConfiguration configuration)
    {
        var tenants = (configuration["FeeScheduleEngine:ContractDualRead:Tenants"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return new ContractDualReadOptions
        {
            Enabled = bool.TryParse(configuration["FeeScheduleEngine:ContractDualRead:Enabled"], out var enabled)
                && enabled,
            Tenants = tenants,
            CanonicalContractCollection =
                configuration["FeeScheduleEngine:CanonicalProviderContractCollection"]
                ?? DefaultCanonicalCollection,
        };
    }
}
