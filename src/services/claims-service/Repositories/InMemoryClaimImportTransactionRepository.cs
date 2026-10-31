using ClaimsService.Models;

namespace ClaimsService.Repositories;

/// <summary>
/// In-process store for tests. Production uses
/// <c>ClaimImportTransactionRepositoryMongo</c>.
/// </summary>
public sealed class InMemoryClaimImportTransactionRepository : IClaimImportTransactionRepository
{
    private readonly List<ClaimImportTransaction> _transactions = [];
    private readonly object _sync = new();

    public Task<ClaimImportTransaction> CreateAsync(ClaimImportTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Id)) txn.Id = Guid.NewGuid().ToString();

        lock (_sync)
        {
            _transactions.Add(txn);
        }

        return Task.FromResult(txn);
    }

    public Task<IReadOnlyList<ClaimImportTransaction>> ListRecentAsync(string tenantId, int limit = 100)
    {
        lock (_sync)
        {
            IReadOnlyList<ClaimImportTransaction> result = _transactions
                .Where(t => t.TenantId == tenantId)
                .OrderByDescending(t => t.ReceivedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<ClaimImportTransaction>> ListWithSnipWarningsAsync(
        string tenantId, string? ruleId = null, int? level = null, string? submitterId = null, int limit = 100)
    {
        lock (_sync)
        {
            IReadOnlyList<ClaimImportTransaction> result = _transactions
                .Where(t => t.TenantId == tenantId)
                .Where(t => submitterId is null || t.SubmitterIdNormalized == submitterId || t.ApplicationSenderCodeNormalized == submitterId)
                .Where(t => t.SnipWarnings.Any(w =>
                    (ruleId is null || w.RuleId == ruleId) && (level is null || w.Level == level)))
                .OrderByDescending(t => t.ReceivedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<ClaimImportTransaction>> ListByImportBatchAsync(
        string tenantId, string importBatchId, int limit, CancellationToken ct = default)
    {
        lock (_sync)
        {
            IReadOnlyList<ClaimImportTransaction> result = _transactions
                .Where(t => t.TenantId == tenantId && t.ImportBatchId == importBatchId)
                .OrderBy(t => t.ReceivedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
