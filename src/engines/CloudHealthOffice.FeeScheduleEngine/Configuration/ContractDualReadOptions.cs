using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.FeeScheduleEngine.Configuration;

/// <summary>
/// Which contract a tenant's claims are paid on, through the rollout of
/// ADR 016 phases 4 and 5.
/// </summary>
public enum ContractPricingMode
{
    /// <summary>
    /// One contract read, one pricing pass, the legacy contract — the engine
    /// exactly as it was before phase 4. The default for every tenant.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Phase 4's window: priced with both contracts, <b>paid on legacy</b>, with
    /// the difference reported per line.
    /// </summary>
    Shadow = 1,

    /// <summary>
    /// Phase 5's cutover: <b>paid on the canonical contract</b>, with the legacy
    /// contract still priced in the shadow for one more cycle so a regression
    /// shows up as a parity delta rather than as a provider's phone call.
    /// </summary>
    Canonical = 2,
}

/// <summary>
/// Where each tenant is in the ADR 016 rollout.
///
/// <para>
/// A tenant moves <see cref="ContractPricingMode.Off"/> →
/// <see cref="ContractPricingMode.Shadow"/> → (one full monthly claim cycle of
/// nothing but matches) → <see cref="ContractPricingMode.Canonical"/>, one
/// tenant at a time. Rollback is the same setting, moved back: no claim is
/// rewritten by it and nothing is migrated either way, so a tenant can be moved
/// back the moment a delta appears.
/// </para>
///
/// <code>
/// FeeScheduleEngine:ContractDualRead:Mode              Off | Shadow | Canonical  (default Off)
/// FeeScheduleEngine:ContractDualRead:Tenants           comma-separated; empty means every tenant
/// FeeScheduleEngine:ContractDualRead:CanonicalTenants  comma-separated; these are paid on canonical
/// FeeScheduleEngine:CanonicalProviderContractCollection                 default ProviderContractsCanonical
/// </code>
///
/// <para>
/// <c>Mode</c> is what the named tenants are in; <c>CanonicalTenants</c> is the
/// per-tenant cutover list, so one deployment can hold a tenant in the window
/// while another is already paid on canonical. A tenant in both is paid on
/// canonical. <c>Enabled</c> is still read as a synonym for
/// <c>Mode=Shadow</c>, for a deployment configured before phase 5.
/// </para>
/// </summary>
public sealed class ContractDualReadOptions
{
    public const string DefaultCanonicalCollection = "ProviderContractsCanonical";

    /// <summary>What the tenants in <see cref="Tenants"/> are in. Off for everyone by default.</summary>
    public ContractPricingMode Mode { get; init; } = ContractPricingMode.Off;

    /// <summary>
    /// The tenants <see cref="Mode"/> applies to. Empty means every tenant,
    /// which is what a single-tenant deployment wants and what the end of the
    /// rollout looks like.
    /// </summary>
    public IReadOnlySet<string> Tenants { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The tenants already cut over, whatever <see cref="Mode"/> says. This is
    /// the per-tenant flag of ADR 016 phase 5, and removing a tenant from it is
    /// the rollback.
    /// </summary>
    public IReadOnlySet<string> CanonicalTenants { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string CanonicalContractCollection { get; init; } = DefaultCanonicalCollection;

    /// <summary>True when anything beyond the pre-phase-4 behaviour is configured at all.</summary>
    public bool IsEnabled => Mode != ContractPricingMode.Off || CanonicalTenants.Count > 0;

    /// <summary>What this tenant's claims are priced and paid with.</summary>
    public ContractPricingMode ModeFor(string tenantId)
    {
        if (CanonicalTenants.Contains(tenantId))
            return ContractPricingMode.Canonical;

        if (Mode == ContractPricingMode.Off)
            return ContractPricingMode.Off;

        return Tenants.Count == 0 || Tenants.Contains(tenantId) ? Mode : ContractPricingMode.Off;
    }

    /// <summary>True when this tenant's claims are priced twice at all.</summary>
    public bool IsInWindow(string tenantId) => ModeFor(tenantId) != ContractPricingMode.Off;

    public static ContractDualReadOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = configuration["FeeScheduleEngine:ContractDualRead:Mode"];
        var parsed = Enum.TryParse<ContractPricingMode>(mode, ignoreCase: true, out var value)
            ? value
            // A deployment configured before phase 5 existed says Enabled=true,
            // and meant the shadow window by it.
            : bool.TryParse(configuration["FeeScheduleEngine:ContractDualRead:Enabled"], out var enabled) && enabled
                ? ContractPricingMode.Shadow
                : ContractPricingMode.Off;

        return new ContractDualReadOptions
        {
            Mode = parsed,
            Tenants = TenantList(configuration["FeeScheduleEngine:ContractDualRead:Tenants"]),
            CanonicalTenants = TenantList(configuration["FeeScheduleEngine:ContractDualRead:CanonicalTenants"]),
            CanonicalContractCollection =
                configuration["FeeScheduleEngine:CanonicalProviderContractCollection"]
                ?? DefaultCanonicalCollection,
        };
    }

    private static IReadOnlySet<string> TenantList(string? configured)
        => (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}
