using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// CHO-native accumulator service backed by MongoDB via
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
            snapshots.Count, LogSanitizer.Sanitize(memberId), benefitPlanId, LogSanitizer.Sanitize(planYear));

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

    public async Task<AccumulatorReversalOutcome> ReverseCommitAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId, string? commitId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commitId))
        {
            await ReverseAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, ct);
            return AccumulatorReversalOutcome.Reversed;
        }
        var tenantId = _tenantContext.TenantId;
        var outcomes = await Task.WhenAll(
            ReverseCommitDocAsync(tenantId, memberId, AccumulatorScope.Individual, benefitPlanId, planYear, claimId, commitId, ct),
            ReverseCommitDocAsync(tenantId, subscriberId, AccumulatorScope.Family, benefitPlanId, planYear, claimId, commitId, ct));
        // A document the claim never wrote (e.g. family, for an
        // individual-only commit) answers null: it does not decide.
        return outcomes.Contains(AccumulatorReversalOutcome.Superseded)
               && !outcomes.Contains(AccumulatorReversalOutcome.Reversed)
            ? AccumulatorReversalOutcome.Superseded
            : AccumulatorReversalOutcome.Reversed;
    }

    /// <summary>
    /// One document's part of <see cref="ReverseCommitAsync"/>, one versioned
    /// write: a newer applied commit of the claim makes it Superseded (no
    /// write); otherwise the claim's transaction of that commit (or a legacy
    /// one without a commit id) is reversed and the commit id fenced.
    /// </summary>
    private async Task<AccumulatorReversalOutcome?> ReverseCommitDocAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        string claimId, string commitId, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var doc = await _repository.GetAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct)
                      ?? CreateEmptyDocument(tenantId, ownerId, scope, benefitPlanId, planYear);
            var tx = doc.Transactions.FirstOrDefault(t => t.ClaimId == claimId && !t.IsReversed);
            if (tx?.CommitId is { Length: > 0 } applied && !string.Equals(applied, commitId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Reversal of commit {CommitId} for claim {ClaimId} superseded on {DocId}: commit {Applied} is applied",
                    LogSanitizer.Sanitize(commitId), LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(doc.Id), LogSanitizer.Sanitize(applied));
                return AccumulatorReversalOutcome.Superseded;
            }
            var fence = !doc.ReversedCommitIds.Contains(commitId);
            if (tx is null && !fence) return null;
            if (tx is not null) ReverseTransaction(doc, tx);
            if (fence) doc.ReversedCommitIds.Add(commitId);
            try
            {
                await _repository.UpsertAsync(doc, ct);
                return tx is null ? null : AccumulatorReversalOutcome.Reversed;
            }
            catch (OptimisticConcurrencyException)
            {
                if (attempt >= MaxConcurrencyRetries - 1) throw;
            }
        }
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
    public async Task<AccumulatorCommitResult> CommitAsync(AccumulatorCommit commit, CancellationToken ct = default)
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

        if (outcomes.Any(o => o.Outcome == AccumulatorCommitOutcome.RefusedCommitReversed))
        {
            _logger.LogInformation(
                "Commit {CommitId} for claim {ClaimId} refused: that commit was reversed (the claim was denied while it was in flight)",
                LogSanitizer.Sanitize(commit.CommitId), LogSanitizer.Sanitize(commit.ClaimId));
            return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedCommitReversed);
        }
        if (outcomes.Any(o => o.Outcome == AccumulatorCommitOutcome.RefusedClaimReversed))
        {
            _logger.LogWarning(
                "Commit {CommitId} for claim {ClaimId} refused: the claim was reversed terminally (void or denial)",
                LogSanitizer.Sanitize(commit.CommitId), LogSanitizer.Sanitize(commit.ClaimId));
            return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedClaimReversed);
        }
        return new AccumulatorCommitResult
        {
            Outcome = outcomes.All(o => o.Outcome == AccumulatorCommitOutcome.AlreadyCommitted)
                ? AccumulatorCommitOutcome.AlreadyCommitted
                : AccumulatorCommitOutcome.Committed,
            Clamped = outcomes.SelectMany(o => o.Clamped).ToList(),
        };
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
                    LogSanitizer.Sanitize(claimId), scope, LogSanitizer.Sanitize(doc.Id));
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
                    transaction.Entries.Count, LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(doc.Id));
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
                        MaxConcurrencyRetries, LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(doc.Id));
                    throw;
                }

                _logger.LogDebug(
                    "Concurrency conflict on attempt {Attempt}/{Max} for claim {ClaimId}, doc {DocId}. Reloading.",
                    attempt + 1, MaxConcurrencyRetries, LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(doc.Id));
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
                        scope, LogSanitizer.Sanitize(ownerId));
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
                    LogSanitizer.Sanitize(claimId), scope, LogSanitizer.Sanitize(doc.Id));
                return;
            }

            if (fence) doc.ReversedClaimIds.Add(claimId);
            if (tx is not null) ReverseTransaction(doc, tx);

            try
            {
                await _repository.UpsertAsync(doc, ct);
                _logger.LogInformation(
                    "Reversed claim {ClaimId} on {DocId} (entries reversed: {Reversed}, fenced: {Fenced})",
                    LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(doc.Id), tx is not null, fence);
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
    private async Task<AccumulatorCommitResult> CommitDocAsync(
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
                return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedClaimReversed);
            if (doc.ReversedCommitIds.Contains(commit.CommitId))
                return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedCommitReversed);

            var own = doc.Transactions.FirstOrDefault(t => t.ClaimId == commit.ClaimId && !t.IsReversed);
            if (own is not null && string.Equals(own.CommitId, commit.CommitId, StringComparison.Ordinal))
            {
                // A repeat: report what the first attempt clamped, so whichever
                // attempt reaches claims-service first records the review.
                return new AccumulatorCommitResult
                {
                    Outcome = AccumulatorCommitOutcome.AlreadyCommitted,
                    Clamped = ClampsOf(own, scope),
                };
            }

            var replaced = replacedClaimId is null
                ? null
                : doc.Transactions.FirstOrDefault(t => t.ClaimId == replacedClaimId && !t.IsReversed);
            // The replaced version is superseded for good: fence it here, in
            // the same write, so a commit of it still in flight (its outbox
            // entry driven late) can never land after this one took it back.
            var fenceReplaced = replacedClaimId is not null && !doc.ReversedClaimIds.Contains(replacedClaimId);

            // Nothing to reverse, fence or apply on this document.
            if (own is null && replaced is null && updates.Count == 0 && !fenceReplaced)
                return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.AlreadyCommitted);

            if (own is not null) ReverseTransaction(doc, own);
            if (replaced is not null) ReverseTransaction(doc, replaced);
            if (fenceReplaced) doc.ReversedClaimIds.Add(replacedClaimId!);

            var clamped = new List<AccumulatorClamp>();
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
                    // Money caps: never past the limit as it stands now.
                    var amount = update.ClampAtLimit is decimal limit && update.Amount > 0
                        ? Math.Min(update.Amount, Math.Max(0, limit - balance.AccumulatedAmount))
                        : update.Amount;
                    if (amount != update.Amount)
                    {
                        clamped.Add(new AccumulatorClamp
                        {
                            Type = update.Type, Scope = update.Scope, NetworkTier = update.NetworkTier,
                            Requested = update.Amount, Applied = amount, Limit = update.ClampAtLimit ?? 0m,
                        });
                    }
                    balance.AccumulatedAmount += amount;
                    transaction.Entries.Add(new AccumulatorTransactionEntry
                    {
                        Type = update.Type.ToString(),
                        NetworkTier = update.NetworkTier.ToString(),
                        AmountApplied = amount,
                        AmountRequested = amount != update.Amount ? update.Amount : null,
                        Limit = amount != update.Amount ? update.ClampAtLimit : null,
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
                    updates.Count, LogSanitizer.Sanitize(commit.ClaimId), LogSanitizer.Sanitize(commit.CommitId), LogSanitizer.Sanitize(doc.Id));
                return new AccumulatorCommitResult { Outcome = AccumulatorCommitOutcome.Committed, Clamped = clamped };
            }
            catch (OptimisticConcurrencyException) when (attempt < MaxConcurrencyRetries - 1)
            {
                _logger.LogDebug(
                    "Concurrency conflict on commit attempt {Attempt}/{Max} for claim {ClaimId}, doc {DocId}. Reloading.",
                    attempt + 1, MaxConcurrencyRetries, LogSanitizer.Sanitize(commit.ClaimId), LogSanitizer.Sanitize(doc.Id));
            }
        }
    }

    /// <summary>The clamps a commit's transaction recorded (see <see cref="AccumulatorTransactionEntry.AmountRequested"/>).</summary>
    private static List<AccumulatorClamp> ClampsOf(AccumulatorTransaction tx, AccumulatorScope scope) =>
        tx.Entries
            .Where(e => e.AmountRequested is not null)
            .Select(e => new AccumulatorClamp
            {
                Type = Enum.TryParse<AccumulatorType>(e.Type, out var t) ? t : default,
                Scope = scope,
                NetworkTier = Enum.TryParse<NetworkTier>(e.NetworkTier, out var n) ? n : NetworkTier.InNetwork,
                Requested = e.AmountRequested!.Value,
                Applied = e.AmountApplied,
                Limit = e.Limit ?? 0m,
            })
            .ToList();

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
