using CloudHealthOffice.Infrastructure.Gateways;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Tenant-safe, vendor-neutral payer identity service. Lookups for a healthcare
/// transaction never guess a payer: missing and ambiguous matches fail
/// explicitly.
/// </summary>
public interface IPayerReferenceService
{
    Task<PayerReference?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Administrative search. Returns every match up to the query limit; it
    /// does not pick a routing target.
    /// </summary>
    Task<IReadOnlyList<PayerReference>> SearchAsync(PayerSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Resolve a single payer from an external identifier. Multiple matches
    /// yield <see cref="PayerResolutionStatus.AmbiguousPayer"/>.
    /// </summary>
    Task<PayerResolution> ResolveExternalIdentifierAsync(
        string system,
        string type,
        string value,
        CancellationToken ct = default);

    Task<IReadOnlyList<PayerTransactionCapability>> GetSupportedTransactionsAsync(
        string payerId, CancellationToken ct = default);

    /// <summary>
    /// Resolve <paramref name="payerId"/> (canonical id, alias, or external
    /// identifier value) for <paramref name="tenantId"/> and the requested
    /// transaction. Tenant overrides are applied; other tenants' overlays are
    /// never consulted.
    /// </summary>
    Task<PayerResolution> ResolveForTransactionAsync(
        string tenantId,
        string? payerId,
        HealthcareTransactionType transaction,
        string? externalSystem = null,
        string? externalType = null,
        CancellationToken ct = default);

    Task<PayerTenantOverride?> GetTenantOverrideAsync(
        string tenantId, string payerId, CancellationToken ct = default);

    Task SaveTenantOverrideAsync(PayerTenantOverride overlay, CancellationToken ct = default);
}

/// <summary>On-demand / scheduled payer-directory refresh.</summary>
public interface IPayerDirectorySynchronizer
{
    Task<PayerDirectorySyncResult> SynchronizeAsync(CancellationToken ct = default);

    Task<PayerDirectorySyncStatus?> GetStatusAsync(CancellationToken ct = default);
}
