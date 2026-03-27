using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// CHO-native accumulator service backed by MongoDB or Cosmos DB via
/// <see cref="IAccumulatorRepository"/>.
///
/// ── Document layout ──────────────────────────────────────────────
/// Two documents are maintained per member per benefit plan year:
///
///   Individual document (scope = Individual, ownerId = memberId)
///     Tracks: IndividualDeductible, IndividualOutOfPocketMax,
///             VisitCount, DollarLimit, DayCount
///
///   Family document (scope = Family, ownerId = subscriberId)
///     Tracks: FamilyDeductible, FamilyOutOfPocketMax
///
/// ── Optimistic concurrency ────────────────────────────────────────
/// If two claims adjudicate for the same member simultaneously (rare
/// but possible with concurrent Argo steps), the second writer will
/// get OptimisticConcurrencyException from the repository. We retry
/// up to MaxConcurrencyRetries times by reloading the document and
/// re-applying the updates on top of the freshest state.
///
/// ── Idempotency ───────────────────────────────────────────────────
/// Before writing, we check whether the claimId already appears in
/// an active (non-reversed) transaction in the document. If it does,
/// we skip the write and return successfully. This ensures that if
/// the Argo adjudication workflow retries the "calculate-payment" step,
/// the accumulators are not double-counted.
///
/// ── Reversal ─────────────────────────────────────────────────────
/// ReverseAsync negates the amounts from each bucket that the original
/// claim applied, marks the transaction IsReversed, and saves.
/// The original amounts are preserved in the transaction log for audit.
/// </summary>
internal class ChoAccumulatorService : IAccumulatorService
{
    private const int MaxConcurrencyRetries = 5;

    private readonly IAccumulatorRepository _repository;
    private readonly IBenefitEngineTenantContext _tenantContext;
    private readonly ILogger<ChoAccumulatorService> _logger;

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public ChoAccumulatorService(
        IAccumulatorRepository repository,
        IBenefitEngineTenantContext tenantContext,
        ILogger<ChoAccumulatorService> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IAccumulatorService
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var tenantId = _tenantContext.TenantId;

        // Load individual and family documents in parallel
        var individualTask = _repository.GetAsync(
            tenantId, memberId, AccumulatorScope.Individual, benefitPlanId, planYear, ct);
        var familyTask = _repository.GetAsync(
            tenantId, subscriberId, AccumulatorScope.Family, benefitPlanId, planYear, ct);

        await Task.WhenAll(individualTask, familyTask);

        var snapshots = new List<AccumulatorSnapshot>();

        if (individualTask.Result is not null)
            snapshots.AddRange(ToSnapshots(individualTask.Result, AccumulatorScope.Individual));

        if (familyTask.Result is not null)
            snapshots.AddRange(ToSnapshots(familyTask.Result, AccumulatorScope.Family));

        _logger.LogDebug(
            "Loaded {Count} accumulator snapshots for member {MemberId}, plan {PlanId} / {PlanYear}",
            snapshots.Count, SanitizeForLog(memberId), benefitPlanId, SanitizeForLog(planYear));

        return snapshots;
    }

    public async Task ApplyUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;

        var tenantId = _tenantContext.TenantId;

        // Partition updates by scope so we write the two documents independently.
        var individualUpdates = updates
            .Where(u => u.Scope == AccumulatorScope.Individual)
            .ToList();

        var familyUpdates = updates
            .Where(u => u.Scope == AccumulatorScope.Family)
            .ToList();

        // Individual and family documents are logically independent within one
        // claim — we can attempt them concurrently. Each has its own retry loop.
        var tasks = new List<Task>();

        if (individualUpdates.Count > 0)
        {
            tasks.Add(ApplyUpdatesToDocAsync(
                tenantId, memberId, AccumulatorScope.Individual,
                benefitPlanId, planYear, claimId, individualUpdates, ct));
        }

        if (familyUpdates.Count > 0)
        {
            tasks.Add(ApplyUpdatesToDocAsync(
                tenantId, subscriberId, AccumulatorScope.Family,
                benefitPlanId, planYear, claimId, familyUpdates, ct));
        }

        await Task.WhenAll(tasks);
    }

    public async Task ReverseAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        var tenantId = _tenantContext.TenantId;

        await Task.WhenAll(
            ReverseDocAsync(tenantId, memberId, AccumulatorScope.Individual,
                benefitPlanId, planYear, claimId, ct),
            ReverseDocAsync(tenantId, subscriberId, AccumulatorScope.Family,
                benefitPlanId, planYear, claimId, ct));
    }

    public async Task ResetForPlanYearAsync(
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var tenantId = _tenantContext.TenantId;

        _logger.LogInformation(
            "Resetting all accumulators for plan {PlanId} / year {PlanYear} (tenant {TenantId})",
            benefitPlanId, planYear, tenantId);

        await _repository.DeleteByPlanYearAsync(tenantId, benefitPlanId, planYear, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — CORE RETRY LOOPS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply accumulator updates to a single document (individual or family)
    /// with optimistic concurrency retry and claimId idempotency check.
    /// </summary>
    private async Task ApplyUpdatesToDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        string claimId, List<AccumulatorUpdate> updates,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var doc = await _repository.GetAsync(
                          tenantId, ownerId, scope, benefitPlanId, planYear, ct)
                      ?? CreateEmptyDocument(tenantId, ownerId, scope, benefitPlanId, planYear);

            // ── Idempotency check ──
            if (doc.Transactions.Any(t => t.ClaimId == claimId && !t.IsReversed))
            {
                _logger.LogInformation(
                    "Claim {ClaimId} already applied to {Scope} accumulator {DocId}. Skipping (idempotent).",
                    SanitizeForLog(claimId), scope, SanitizeForLog(doc.Id));
                return;
            }

            // ── Apply updates to balance buckets ──
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

            // ── Persist with concurrency guard ──
            try
            {
                await _repository.UpsertAsync(doc, ct);
                _logger.LogDebug(
                    "Applied {EntryCount} accumulator entries for claim {ClaimId} to {DocId}",
                    transaction.Entries.Count, SanitizeForLog(claimId), SanitizeForLog(doc.Id));
                return;
            }
            catch (OptimisticConcurrencyException)
            {
                if (attempt == MaxConcurrencyRetries - 1)
                {
                    _logger.LogError(
                        "Optimistic concurrency failure after {Attempts} retries " +
                        "for claim {ClaimId}, document {DocId}. " +
                        "This may indicate extremely high concurrent adjudication volume.",
                        MaxConcurrencyRetries, SanitizeForLog(claimId), SanitizeForLog(doc.Id));
                    throw;
                }

                _logger.LogDebug(
                    "Concurrency conflict on attempt {Attempt}/{Max} for claim {ClaimId}, doc {DocId}. Reloading.",
                    attempt + 1, MaxConcurrencyRetries, SanitizeForLog(claimId), SanitizeForLog(doc.Id));
            }
        }
    }

    /// <summary>
    /// Reverse a claim's accumulator contributions on a single document,
    /// with optimistic concurrency retry.
    /// </summary>
    private async Task ReverseDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        string claimId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var doc = await _repository.GetAsync(
                tenantId, ownerId, scope, benefitPlanId, planYear, ct);

            if (doc is null)
            {
                _logger.LogDebug(
                    "No {Scope} accumulator document found for owner {OwnerId}. Nothing to reverse.",
                    scope, ownerId);
                return;
            }

            var tx = doc.Transactions
                .FirstOrDefault(t => t.ClaimId == claimId && !t.IsReversed);

            if (tx is null)
            {
                _logger.LogInformation(
                    "Claim {ClaimId} has no active transaction in {Scope} doc {DocId}. " +
                    "Already reversed or never applied.",
                    claimId, scope, doc.Id);
                return;
            }

            // ── Negate each accumulated amount ──
            foreach (var entry in tx.Entries)
            {
                var balance = doc.Balances.FirstOrDefault(
                    b => b.Type == entry.Type && b.NetworkTier == entry.NetworkTier);

                if (balance is not null)
                {
                    // Clamp at zero — accumulated amount should never go negative
                    balance.AccumulatedAmount = Math.Max(0,
                        balance.AccumulatedAmount - entry.AmountApplied);
                }
            }

            tx.IsReversed = true;
            tx.ReversedAt = DateTime.UtcNow;

            try
            {
                await _repository.UpsertAsync(doc, ct);
                _logger.LogInformation(
                    "Reversed claim {ClaimId} accumulator entries on {DocId}",
                    claimId, doc.Id);
                return;
            }
            catch (OptimisticConcurrencyException)
            {
                if (attempt == MaxConcurrencyRetries - 1) throw;

                _logger.LogDebug(
                    "Concurrency conflict on reversal attempt {Attempt}/{Max} for claim {ClaimId}. Reloading.",
                    attempt + 1, MaxConcurrencyRetries, claimId);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static IEnumerable<AccumulatorSnapshot> ToSnapshots(
        AccumulatorDocument doc, AccumulatorScope scope)
    {
        foreach (var balance in doc.Balances)
        {
            if (!Enum.TryParse<AccumulatorType>(balance.Type, out var type)) continue;
            if (!Enum.TryParse<NetworkTier>(balance.NetworkTier, out var tier)) continue;

            yield return new AccumulatorSnapshot
            {
                Type = type,
                Scope = scope,
                NetworkTier = tier,
                LimitAmount = balance.LimitAmount,
                AccumulatedAmountAfter = balance.AccumulatedAmount
            };
        }
    }

    private static AccumulatorBalance GetOrCreateBalance(
        AccumulatorDocument doc, AccumulatorType type, NetworkTier tier)
    {
        var typeName = type.ToString();
        var tierName = tier.ToString();

        var balance = doc.Balances
            .FirstOrDefault(b => b.Type == typeName && b.NetworkTier == tierName);

        if (balance is null)
        {
            balance = new AccumulatorBalance
            {
                Type = typeName,
                NetworkTier = tierName,
                LimitAmount = 0, // Authoritative limit is in BenefitPlanConfig
                AccumulatedAmount = 0
            };
            doc.Balances.Add(balance);
        }

        return balance;
    }

    private static AccumulatorDocument CreateEmptyDocument(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear)
    {
        return new AccumulatorDocument
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
}
