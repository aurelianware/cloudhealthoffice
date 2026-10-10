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

    public async Task<IReadOnlyList<AccumulatorUpdate>> GetClaimUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        var tenantId = _tenantContext.TenantId;
        var updates = new List<AccumulatorUpdate>();
        foreach (var (owner, scope) in new[] { (memberId, AccumulatorScope.Individual), (subscriberId, AccumulatorScope.Family) })
        {
            var doc = await _repository.GetAsync(tenantId, owner, scope, benefitPlanId, planYear, ct);
            var tx = doc?.Transactions.FirstOrDefault(t => t.ClaimId == claimId && !t.IsReversed);
            if (tx is null) continue;
            foreach (var e in tx.Entries)
            {
                if (!Enum.TryParse<AccumulatorType>(e.Type, out var type)) continue;
                if (!Enum.TryParse<NetworkTier>(e.NetworkTier, out var tier)) continue;
                updates.Add(new AccumulatorUpdate
                {
                    Type = type, Scope = scope, NetworkTier = tier, Amount = e.AmountApplied, Source = e.Source,
                });
            }
        }
        return updates;
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

    public async Task ReverseTerminallyAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        var tenantId = _tenantContext.TenantId;

        await Task.WhenAll(
            ReverseDocAsync(tenantId, memberId, AccumulatorScope.Individual,
                benefitPlanId, planYear, claimId, ct, terminal: true),
            ReverseDocAsync(tenantId, subscriberId, AccumulatorScope.Family,
                benefitPlanId, planYear, claimId, ct, terminal: true));
    }

    /// <summary>
    /// Writes a prepared commit, one versioned write per document (individual,
    /// family): in that write the claim's own active transaction (an earlier
    /// adjudication of it) and the replaced claim's are reversed, the new
    /// updates applied (deductible clamped at write time) and the transaction
    /// tagged with the commit id. A document that fences the claim
    /// (<see cref="AccumulatorDocument.ReversedClaimIds"/>) refuses it: the
    /// fence and the apply are decided by the same version-checked write, so
    /// a denial or void that lands first always wins.
    /// </summary>
    public async Task<AccumulatorCommitOutcome> CommitAsync(AccumulatorCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (string.IsNullOrWhiteSpace(commit.ClaimId))
            throw new ArgumentException("The commit names no claim.", nameof(commit));
        var tenantId = _tenantContext.TenantId;
        var replaced = string.IsNullOrWhiteSpace(commit.ReplacesClaimId)
                       || string.Equals(commit.ReplacesClaimId, commit.ClaimId, StringComparison.Ordinal)
            ? null
            : commit.ReplacesClaimId;

        var outcomes = await Task.WhenAll(
            CommitDocAsync(tenantId, commit.MemberId, AccumulatorScope.Individual, commit, replaced,
                commit.Updates.Where(u => u.Scope == AccumulatorScope.Individual).ToList(), ct),
            CommitDocAsync(tenantId, commit.SubscriberId, AccumulatorScope.Family, commit, replaced,
                commit.Updates.Where(u => u.Scope == AccumulatorScope.Family).ToList(), ct));

        if (outcomes.Contains(AccumulatorCommitOutcome.RefusedClaimReversed))
        {
            _logger.LogWarning(
                "Commit {CommitId} for claim {ClaimId} refused: the claim was reversed terminally (void or denial)",
                SanitizeForLog(commit.CommitId), SanitizeForLog(commit.ClaimId));
            return AccumulatorCommitOutcome.RefusedClaimReversed;
        }
        return outcomes.All(o => o == AccumulatorCommitOutcome.AlreadyCommitted)
            ? AccumulatorCommitOutcome.AlreadyCommitted
            : AccumulatorCommitOutcome.Committed;
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

            // The terminal fence (ReversedClaimIds) governs CommitAsync, the
            // claims pipeline's write. This direct Production write keeps its
            // earlier semantics for other callers (a void then a
            // re-adjudication of the same claim id applies again).

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
                // Deductible: never past the plan limit as it stands at write
                // time (a concurrent claim may have met it since this claim's
                // working set was read; a conflict reloads and re-clamps).
                var amount = update.ClampAtLimit is decimal limit && update.Amount > 0
                    ? Math.Min(update.Amount, Math.Max(0, limit - balance.AccumulatedAmount))
                    : update.Amount;
                balance.AccumulatedAmount += amount;

                transaction.Entries.Add(new AccumulatorTransactionEntry
                {
                    Type = update.Type.ToString(),
                    NetworkTier = update.NetworkTier.ToString(),
                    AmountApplied = amount,
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
        string claimId, CancellationToken ct, bool terminal = false)
    {
        for (int attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var doc = await _repository.GetAsync(
                tenantId, ownerId, scope, benefitPlanId, planYear, ct);

            if (doc is null)
            {
                if (!terminal)
                {
                    _logger.LogDebug(
                        "No {Scope} accumulator document found for owner {OwnerId}. Nothing to reverse.",
                        scope, SanitizeForLog(ownerId));
                    return;
                }
                // Nothing applied yet, but the fence is still written: a
                // commit already in flight must find it.
                doc = CreateEmptyDocument(tenantId, ownerId, scope, benefitPlanId, planYear);
            }

            var fence = terminal && !doc.ReversedClaimIds.Contains(claimId);
            var tx = doc.Transactions
                .FirstOrDefault(t => t.ClaimId == claimId && !t.IsReversed);

            if (tx is null && !fence)
            {
                _logger.LogInformation(
                    "Claim {ClaimId} has no active transaction in {Scope} doc {DocId}. " +
                    "Already reversed or never applied.",
                    SanitizeForLog(claimId), scope, SanitizeForLog(doc.Id));
                return;
            }

            if (fence) doc.ReversedClaimIds.Add(claimId);
            if (tx is not null) ReverseTransaction(doc, tx);

            try
            {
                await _repository.UpsertAsync(doc, ct);
                _logger.LogInformation(
                    "Reversed claim {ClaimId} on {DocId} (entries reversed: {Reversed}, fenced: {Fenced})",
                    SanitizeForLog(claimId), SanitizeForLog(doc.Id), tx is not null, fence);
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

    /// <summary>
    /// One document's part of <see cref="CommitAsync"/>, as a single
    /// versioned write with optimistic-concurrency retry.
    /// </summary>
    private async Task<AccumulatorCommitOutcome> CommitDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        AccumulatorCommit commit, string? replacedClaimId, List<AccumulatorUpdate> updates,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var doc = await _repository.GetAsync(
                          tenantId, ownerId, scope, commit.BenefitPlanId, commit.PlanYear, ct)
                      ?? CreateEmptyDocument(tenantId, ownerId, scope, commit.BenefitPlanId, commit.PlanYear);

            if (doc.ReversedClaimIds.Contains(commit.ClaimId))
                return AccumulatorCommitOutcome.RefusedClaimReversed;

            var own = doc.Transactions.FirstOrDefault(t => t.ClaimId == commit.ClaimId && !t.IsReversed);
            if (own is not null && string.Equals(own.CommitId, commit.CommitId, StringComparison.Ordinal))
                return AccumulatorCommitOutcome.AlreadyCommitted;

            var replaced = replacedClaimId is null
                ? null
                : doc.Transactions.FirstOrDefault(t => t.ClaimId == replacedClaimId && !t.IsReversed);

            // Nothing to reverse and nothing to apply on this document.
            if (own is null && replaced is null && updates.Count == 0)
                return AccumulatorCommitOutcome.AlreadyCommitted;

            if (own is not null) ReverseTransaction(doc, own);
            if (replaced is not null) ReverseTransaction(doc, replaced);

            if (updates.Count > 0)
            {
                var transaction = new AccumulatorTransaction
                {
                    ClaimId = commit.ClaimId,
                    AppliedAt = DateTime.UtcNow,
                    CommitId = commit.CommitId,
                    Entries = []
                };
                foreach (var update in updates)
                {
                    var balance = GetOrCreateBalance(doc, update.Type, update.NetworkTier);
                    // Deductible: never past the limit as it stands now.
                    var amount = update.ClampAtLimit is decimal limit && update.Amount > 0
                        ? Math.Min(update.Amount, Math.Max(0, limit - balance.AccumulatedAmount))
                        : update.Amount;
                    balance.AccumulatedAmount += amount;
                    transaction.Entries.Add(new AccumulatorTransactionEntry
                    {
                        Type = update.Type.ToString(),
                        NetworkTier = update.NetworkTier.ToString(),
                        AmountApplied = amount,
                        Source = update.Source
                    });
                }
                doc.Transactions.Add(transaction);
            }

            try
            {
                await _repository.UpsertAsync(doc, ct);
                _logger.LogDebug(
                    "Committed {Count} accumulator entries for claim {ClaimId} (commit {CommitId}) to {DocId}",
                    updates.Count, SanitizeForLog(commit.ClaimId), SanitizeForLog(commit.CommitId), SanitizeForLog(doc.Id));
                return AccumulatorCommitOutcome.Committed;
            }
            catch (OptimisticConcurrencyException) when (attempt < MaxConcurrencyRetries - 1)
            {
                _logger.LogDebug(
                    "Concurrency conflict on commit attempt {Attempt}/{Max} for claim {ClaimId}, doc {DocId}. Reloading.",
                    attempt + 1, MaxConcurrencyRetries, SanitizeForLog(commit.ClaimId), SanitizeForLog(doc.Id));
            }
        }
    }

    /// <summary>Negates a transaction's entries (never below zero) and marks it reversed.</summary>
    private static void ReverseTransaction(AccumulatorDocument doc, AccumulatorTransaction tx)
    {
        foreach (var entry in tx.Entries)
        {
            var balance = doc.Balances.FirstOrDefault(
                b => b.Type == entry.Type && b.NetworkTier == entry.NetworkTier);
            if (balance is not null)
                balance.AccumulatedAmount = Math.Max(0, balance.AccumulatedAmount - entry.AmountApplied);
        }
        tx.IsReversed = true;
        tx.ReversedAt = DateTime.UtcNow;
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
