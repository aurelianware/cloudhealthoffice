namespace CloudHealthOffice.OperatingMode;

/// <summary>
/// Fetches the per-tenant operating mode configuration at runtime.
/// Used by the AdjudicationController to determine whether each engine
/// runs in Augment (shadow alongside QNXT) or Replace (CHO authoritative) mode.
///
/// Implementations should cache aggressively — tenant mode changes are infrequent
/// (admin action), so a 5-minute TTL is typical.
/// </summary>
public interface IOperatingModeProvider
{
    /// <summary>
    /// Retrieve the operating mode configuration for a tenant.
    /// Returns a default configuration (all engines in Replace mode) when the
    /// tenant has no explicit operating mode configuration.
    /// </summary>
    Task<OperatingModeConfiguration> GetConfigurationAsync(
        string tenantId,
        CancellationToken ct = default);
}
