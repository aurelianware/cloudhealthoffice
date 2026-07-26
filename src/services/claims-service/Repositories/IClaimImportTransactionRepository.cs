using ClaimsService.Models;

namespace ClaimsService.Repositories;

public interface IClaimImportTransactionRepository
{
    Task<ClaimImportTransaction> CreateAsync(ClaimImportTransaction txn);

    /// <summary>Most recent transactions for a tenant, newest first — the admin-console read path.</summary>
    Task<IReadOnlyList<ClaimImportTransaction>> ListRecentAsync(string tenantId, int limit = 100);
}
