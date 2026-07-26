using ClaimsService.Models;

namespace ClaimsService.Repositories;

/// <summary>
/// In-process fallback for Cosmos-configured deployments — mirrors
/// <c>InMemoryMassAdjudicationRunRepository</c>'s precedent for capabilities
/// that haven't gotten a Cosmos-backed implementation yet. Per-pod only;
/// acceptable because this collection backs an admin diagnostic view, not
/// anything transactionally load-bearing.
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
}
