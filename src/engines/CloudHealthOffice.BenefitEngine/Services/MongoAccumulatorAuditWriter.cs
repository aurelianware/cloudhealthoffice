using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Writes the durable accumulator audit trail to MongoDB or Cosmos DB.
///
/// Used as the <see cref="IAccumulatorAuditWriter"/> behind the Redis accumulator
/// service. The Redis service fires this off asynchronously after each adjudication
/// — it is non-blocking and does not affect the adjudication latency path.
///
/// ── What this writes ──────────────────────────────────────────────
///
/// Same AccumulatorDocument structure used by ChoAccumulatorService:
/// balances + transaction log (one entry per claim). This provides:
///   - Full audit trail for compliance
///   - Source-of-truth recovery if Redis is unavailable/flushed
///   - Portal display of member benefit usage history
///
/// ── Relationship with RedisAccumulatorService ─────────────────────
///
/// Redis is the hot path (sub-millisecond reads, atomic HINCRBYFLOAT).
/// This class is the cold path (durable, queryable, for audit/portal).
/// Both stay in sync via the fire-and-forget write in ApplyUpdatesAsync.
///
/// If an audit write fails (network blip, DB unavailable), it is logged
/// and silently swallowed. The Redis state is the authoritative current
/// balance; the audit trail self-heals on the next cache rebuild.
/// </summary>
public class MongoAccumulatorAuditWriter : IAccumulatorAuditWriter
{
    private const int MaxRetries = 3;

    private readonly IAccumulatorRepository _repository;
    private readonly ILogger<MongoAccumulatorAuditWriter> _logger;

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public MongoAccumulatorAuditWriter(
        IAccumulatorRepository repository,
        ILogger<MongoAccumulatorAuditWriter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task WriteAuditAsync(
        string tenantId, string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId, IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;

        var individualUpdates = updates.Where(u => u.Scope == AccumulatorScope.Individual).ToList();
        var familyUpdates = updates.Where(u => u.Scope == AccumulatorScope.Family).ToList();

        if (individualUpdates.Count > 0)
            await WriteUpdatesToDocAsync(tenantId, memberId, AccumulatorScope.Individual,
                benefitPlanId, planYear, claimId, individualUpdates, ct);

        if (familyUpdates.Count > 0)
            await WriteUpdatesToDocAsync(tenantId, subscriberId, AccumulatorScope.Family,
                benefitPlanId, planYear, claimId, familyUpdates, ct);
    }

    public async Task WriteReversalAuditAsync(
        string tenantId, string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId, CancellationToken ct = default)
    {
        await ReverseDocAsync(tenantId, memberId, AccumulatorScope.Individual,
            benefitPlanId, planYear, claimId, ct);
        await ReverseDocAsync(tenantId, subscriberId, AccumulatorScope.Family,
            benefitPlanId, planYear, claimId, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — mirrors ChoAccumulatorService but takes explicit tenantId
    // ═══════════════════════════════════════════════════════════════════

    private async Task WriteUpdatesToDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        string claimId, List<AccumulatorUpdate> updates, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var doc = await _repository.GetAsync(
                          tenantId, ownerId, scope, benefitPlanId, planYear, ct)
                      ?? CreateEmptyDocument(tenantId, ownerId, scope, benefitPlanId, planYear);

            // Idempotency: skip if already written
            if (doc.Transactions.Any(t => t.ClaimId == claimId && !t.IsReversed))
                return;

            var transaction = new AccumulatorTransaction
            {
                ClaimId = claimId,
                AppliedAt = DateTime.UtcNow,
                Entries = []
            };

            foreach (var update in updates)
            {
                var balance = GetOrCreateBalance(doc, update.Type, update.NetworkTier);
                balance.AccumulatedAmount += update.Amount;

                transaction.Entries.Add(new AccumulatorTransactionEntry
                {
                    Type = update.Type.ToString(),
                    NetworkTier = update.NetworkTier.ToString(),
                    AmountApplied = update.Amount,
                    Source = update.Source
                });
            }

            doc.Transactions.Add(transaction);

            try
            {
                await _repository.UpsertAsync(doc, ct);
                return;
            }
            catch (OptimisticConcurrencyException)
            {
                if (attempt == MaxRetries - 1)
                {
                    _logger.LogWarning(
                        "Audit write failed after {Retries} retries for claim {ClaimId}, doc {DocId}. " +
                        "Audit trail will self-heal on next cache rebuild.",
                        MaxRetries, SanitizeForLog(claimId), SanitizeForLog(doc.Id));
                    throw;
                }
            }
        }
    }

    private async Task ReverseDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        string claimId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var doc = await _repository.GetAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct);
            if (doc is null) return;

            var tx = doc.Transactions.FirstOrDefault(t => t.ClaimId == claimId && !t.IsReversed);
            if (tx is null) return;

            foreach (var entry in tx.Entries)
            {
                var balance = doc.Balances.FirstOrDefault(
                    b => b.Type == entry.Type && b.NetworkTier == entry.NetworkTier);
                if (balance is not null)
                    balance.AccumulatedAmount = Math.Max(0, balance.AccumulatedAmount - entry.AmountApplied);
            }

            tx.IsReversed = true;
            tx.ReversedAt = DateTime.UtcNow;

            try
            {
                await _repository.UpsertAsync(doc, ct);
                return;
            }
            catch (OptimisticConcurrencyException)
            {
                if (attempt == MaxRetries - 1) throw;
            }
        }
    }

    private static AccumulatorBalance GetOrCreateBalance(
        AccumulatorDocument doc, AccumulatorType type, NetworkTier tier)
    {
        var typeName = type.ToString();
        var tierName = tier.ToString();

        var balance = doc.Balances.FirstOrDefault(b => b.Type == typeName && b.NetworkTier == tierName);
        if (balance is null)
        {
            balance = new AccumulatorBalance { Type = typeName, NetworkTier = tierName };
            doc.Balances.Add(balance);
        }
        return balance;
    }

    private static AccumulatorDocument CreateEmptyDocument(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear) => new()
    {
        Id = AccumulatorDocument.MakeId(tenantId, scope.ToString(), ownerId, benefitPlanId, planYear),
        TenantId = tenantId,
        OwnerId = ownerId,
        Scope = scope.ToString(),
        BenefitPlanId = benefitPlanId,
        PlanYear = planYear,
        Version = 0
    };
}
