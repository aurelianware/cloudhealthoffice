namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Persistence for canonical payer records, tenant overlays, and sync status.
/// Global payer records are not tenant-scoped; overlays are keyed by tenant id
/// and must never be returned for a different tenant.
/// </summary>
internal interface IPayerReferenceStore
{
    Task<PayerReference?> GetByIdAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<PayerReference>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct);

    Task<IReadOnlyList<PayerReference>> FindExactAsync(string normalizedToken, CancellationToken ct);

    Task<IReadOnlyList<PayerReference>> SearchAsync(PayerSearchQuery query, CancellationToken ct);

    Task UpsertAsync(PayerReference payer, CancellationToken ct);

    Task UpsertManyAsync(IReadOnlyList<PayerReference> payers, CancellationToken ct);

    /// <summary>
    /// Mark source-owned payers whose ids are not in
    /// <paramref name="presentIds"/> as inactive. Returns how many were disabled.
    /// </summary>
    Task<int> DisableMissingFromSourceAsync(
        string source, IReadOnlyCollection<string> presentIds, DateTimeOffset at, CancellationToken ct);

    Task<PayerTenantOverride?> GetTenantOverrideAsync(string tenantId, string payerId, CancellationToken ct);

    Task UpsertTenantOverrideAsync(PayerTenantOverride overlay, CancellationToken ct);

    Task<IReadOnlyList<PayerTenantOverride>> ListTenantOverridesAsync(string tenantId, CancellationToken ct);

    Task<PayerDirectorySyncStatus?> GetSyncStatusAsync(string source, CancellationToken ct);

    Task SaveSyncStatusAsync(PayerDirectorySyncStatus status, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}
