using ClaimsService.Models;

namespace ClaimsService.Repositories;

public interface IClaimImportTransactionRepository
{
    Task<ClaimImportTransaction> CreateAsync(ClaimImportTransaction txn);

    /// <summary>Most recent transactions for a tenant, newest first — the admin-console read path.</summary>
    Task<IReadOnlyList<ClaimImportTransaction>> ListRecentAsync(string tenantId, int limit = 100);

    /// <summary>
    /// Transactions carrying a SNIP warning, newest first, optionally narrowed
    /// to one rule id, SNIP level and/or submitter. <paramref name="submitterId"/>
    /// is already normalized (trimmed, upper-case) and matches the normalized
    /// ISA06 or GS02.
    /// </summary>
    Task<IReadOnlyList<ClaimImportTransaction>> ListWithSnipWarningsAsync(
        string tenantId, string? ruleId = null, int? level = null, string? submitterId = null, int limit = 100);
}
