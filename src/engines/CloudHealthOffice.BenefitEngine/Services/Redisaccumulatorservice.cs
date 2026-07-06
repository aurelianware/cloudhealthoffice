using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Redis-backed accumulator service following the QNXT pattern: accumulators
/// are calculated at runtime from claim history, never stored as mutable state.
///
/// ── Architecture ────────────────────────────────────────────────────
///
///   Source of truth:  Finalized claim lines in claims-service
///   Hot cache:        Redis hash per member per plan year
///   Audit trail:      MongoDB/Cosmos AccumulatorDocument (async, non-blocking)
///
/// ── Why this approach ──────────────────────────────────────────────
///
/// Storing accumulator balances as mutable documents creates concurrency
/// problems: two claims for the same member adjudicating simultaneously
/// require optimistic concurrency, retry loops, and idempotency guards.
/// QNXT avoids this entirely by calculating accumulators at runtime.
///
/// Redis gives us the best of both worlds:
///   - HINCRBYFLOAT is atomic — no read-modify-write race
///   - HGETALL is sub-millisecond — within the 500ms adjudication target
///   - TTL handles annual reset — no batch delete job
///   - Cache miss → recalculate from claims → populate Redis
///
/// ── Redis key layout ──────────────────────────────────────────────
///
///   Hash key: "accum:{tenantId}:{memberId}:{planId}:{planYear}"
///   Fields:   "{AccumulatorType}:{NetworkTier}"  →  accumulated amount
///
///   Example:
///     accum:tenant-001:M12345:plan-abc:2026
///       IndividualDeductible:InNetwork        → "750.00"
///       IndividualDeductible:OutOfNetwork      → "0.00"
///       IndividualOopMax:InNetwork             → "1250.00"
///       VisitCount:98                          → "3"
///
///   Family aggregates use subscriberId instead of memberId:
///     accum:tenant-001:FAM:S67890:plan-abc:2026
///       FamilyDeductible:InNetwork             → "1500.00"
///       FamilyOopMax:InNetwork                 → "2100.00"
///
/// ── Cache miss / rebuild ──────────────────────────────────────────
///
/// On cache miss (key doesn't exist), the service calls
/// IClaimsAccumulatorSource to sum finalized claim lines for the
/// member/plan/year. This interface is implemented by the host service
/// (benefit-plan-service or claims-service) and queries the claim store.
///
/// ── Reversal ──────────────────────────────────────────────────────
///
/// When a claim is voided, HINCRBYFLOAT with negative amounts adjusts
/// the cache atomically. If the key was evicted, the next read rebuilds
/// from claim history (which no longer includes the voided claim).
/// Either way, the result is correct.
/// </summary>
/// <remarks>
/// This service intentionally does NOT use the shared
/// <see cref="CloudHealthOffice.Infrastructure.Caching.ICacheProvider"/>
/// abstraction introduced in Addendum A.7.2. Accumulators use Redis hashes
/// with server-side atomic <c>HINCRBYFLOAT</c> to avoid read-modify-write
/// races between concurrent claim adjudications — behaviour that a
/// string-K/V cache abstraction cannot express. Moving this class onto
/// <c>ICacheProvider</c> would either require expanding the interface with
/// hash operations (leaking Redis semantics into a neutral abstraction) or
/// JSON-serializing the full hash on every update (reintroducing the race
/// we designed around). Neither is acceptable.
///
/// The pattern: <c>ICacheProvider</c> is for caches — reads dominated, data
/// is regeneratable, loss is recoverable. This service uses Redis as
/// structured hot storage with domain-specific atomic semantics, which is
/// a different job. See <c>docs/architecture/shared-cache.md</c> for the
/// decision tree and the second deliberate exception
/// (<c>RedisPaRuleRepository</c> for SCAN-based invalidation).
/// </remarks>
public class RedisAccumulatorService : IAccumulatorService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IClaimsAccumulatorSource _claimsSource;
    private readonly IAccumulatorAuditWriter? _auditWriter;
    private readonly IBenefitEngineTenantContext _tenantContext;
    private readonly ILogger<RedisAccumulatorService> _logger;

    /// <summary>
    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    /// TTL for accumulator keys. Set to cover the plan year plus a grace
    /// period for late claims (run-out). 14 months covers a calendar-year
    /// plan with 2 months of run-out.
    /// </summary>
    private static readonly TimeSpan DefaultKeyTtl = TimeSpan.FromDays(425);
    private static readonly RedisValue EmptySnapshotField = "__empty";

    public RedisAccumulatorService(
        IConnectionMultiplexer redis,
        IClaimsAccumulatorSource claimsSource,
        IBenefitEngineTenantContext tenantContext,
        ILogger<RedisAccumulatorService> logger,
        IAccumulatorAuditWriter? auditWriter = null)
    {
        _redis = redis;
        _claimsSource = claimsSource;
        _tenantContext = tenantContext;
        _logger = logger;
        _auditWriter = auditWriter;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IAccumulatorService — READ
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var snapshots = new List<AccumulatorSnapshot>();

        // Load individual and family accumulators independently. Running
        // both reads in parallel avoids paying two Redis round trips serially.
        var individualTask = GetOrRebuildAsync(
            db, memberId, AccumulatorScope.Individual, benefitPlanId, planYear, ct);
        var familyTask = GetOrRebuildAsync(
            db, subscriberId, AccumulatorScope.Family, benefitPlanId, planYear, ct);

        await Task.WhenAll(individualTask, familyTask);

        var individualSnapshots = await individualTask;
        var familySnapshots = await familyTask;
        snapshots.AddRange(individualSnapshots);
        snapshots.AddRange(familySnapshots);

        _logger.LogDebug(
            "Loaded {Count} accumulator snapshots for member {MemberId}, plan {PlanId}/{PlanYear}",
            snapshots.Count, SanitizeForLog(memberId), benefitPlanId, SanitizeForLog(planYear));

        return snapshots;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IAccumulatorService — WRITE (after adjudication)
    // ═══════════════════════════════════════════════════════════════════

    public async Task ApplyUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;

        var db = _redis.GetDatabase();

        // Partition updates by scope
        var individualUpdates = updates.Where(u => u.Scope == AccumulatorScope.Individual).ToList();
        var familyUpdates = updates.Where(u => u.Scope == AccumulatorScope.Family).ToList();

        // Apply individual increments atomically via Redis pipeline
        if (individualUpdates.Count > 0)
        {
            var key = MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear);
            await ApplyIncrementsAsync(db, key, individualUpdates);
        }

        // Apply family increments
        if (familyUpdates.Count > 0)
        {
            var key = MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear);
            await ApplyIncrementsAsync(db, key, familyUpdates);
        }

        _logger.LogDebug(
            "Applied {Count} accumulator updates for claim {ClaimId} (member {MemberId})",
            updates.Count, SanitizeForLog(claimId), SanitizeForLog(memberId));

        // Fire-and-forget: write audit trail to MongoDB/Cosmos
        // This is non-blocking — adjudication doesn't wait for the audit write
        if (_auditWriter is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _auditWriter.WriteAuditAsync(
                        _tenantContext.TenantId, memberId, subscriberId,
                        benefitPlanId, planYear, claimId, updates, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to write accumulator audit for claim {ClaimId}. " +
                        "Audit trail will self-heal on next rebuild.",
                        SanitizeForLog(claimId));
                }
            }, ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IAccumulatorService — REVERSAL
    // ═══════════════════════════════════════════════════════════════════

    public async Task ReverseAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        // Option A: Invalidate the cache and let it rebuild from claim history
        //           (which no longer includes the voided claim). Simple and correct.
        //
        // Option B: Decrement the specific amounts this claim applied.
        //           Faster but requires knowing what amounts to reverse.
        //
        // We use Option A because it's always correct regardless of whether
        // the cache exists. The next GetAccumulatorsAsync call will rebuild
        // from the claims store, which won't include the reversed claim.

        var db = _redis.GetDatabase();

        var individualKey = MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear);
        var familyKey = MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear);

        await db.KeyDeleteAsync(new RedisKey[] { individualKey, familyKey });

        _logger.LogInformation(
            "Invalidated accumulator cache for claim reversal {ClaimId} (member {MemberId})",
            claimId, memberId);

        // Write reversal to audit trail
        if (_auditWriter is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _auditWriter.WriteReversalAuditAsync(
                        _tenantContext.TenantId, memberId, subscriberId,
                        benefitPlanId, planYear, claimId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to write reversal audit for claim {ClaimId}", claimId);
                }
            }, ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IAccumulatorService — ANNUAL RESET
    // ═══════════════════════════════════════════════════════════════════

    public async Task ResetForPlanYearAsync(
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        // Redis keys auto-expire via TTL, so there's nothing to "reset"
        // in the cache. The keys for the old plan year will expire naturally.
        //
        // For explicit cleanup (e.g., regulatory requirement to purge),
        // we'd scan for keys matching the pattern. But Redis SCAN is
        // expensive at scale, so we rely on TTL expiry for normal operation.
        //
        // The audit trail in MongoDB/Cosmos is retained for compliance.

        _logger.LogInformation(
            "Plan year reset for plan {PlanId}/{PlanYear}. " +
            "Redis keys will expire via TTL. Audit trail retained.",
            benefitPlanId, planYear);

        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — CACHE READ / REBUILD
    // ═══════════════════════════════════════════════════════════════════

    private async Task<List<AccumulatorSnapshot>> GetOrRebuildAsync(
        IDatabase db, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct)
    {
        var key = MakeKey(ownerId, scope, benefitPlanId, planYear);

        // Try Redis first
        var entries = await db.HashGetAllAsync(key);

        if (entries.Length > 0)
        {
            return ParseSnapshots(entries, scope);
        }

        // Cache miss — rebuild from claim history
        _logger.LogDebug(
            "Cache miss for {Key}. Rebuilding from claim history.", key.ToString());

        var (fetchSuccess, computed) = await _claimsSource.CalculateAccumulatorsAsync(
            _tenantContext.TenantId, ownerId, scope, benefitPlanId, planYear, ct);

        if (!fetchSuccess)
        {
            // Source was unavailable — do not cache; the next read will retry.
            _logger.LogWarning(
                "Accumulator source unavailable for {Key}. Skipping Redis cache population.",
                key.ToString());
            return [];
        }

        if (computed.Count == 0)
        {
            // Authoritatively empty: owner has no accumulator history.
            await MarkEmptySnapshotAsync(db, key);
            return [];
        }

        // Populate Redis
        var hashEntries = computed.Select(s =>
            new HashEntry(
                MakeField(s.Type, s.NetworkTier),
                (double)s.AccumulatedAmountAfter))
            .ToArray();

        await db.HashSetAsync(key, hashEntries);
        await db.KeyExpireAsync(key, DefaultKeyTtl);

        _logger.LogDebug(
            "Rebuilt {Count} accumulators for {Key} from claim history.",
            computed.Count, key.ToString());

        return computed.ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — ATOMIC INCREMENTS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ApplyIncrementsAsync(
        IDatabase db, RedisKey key, List<AccumulatorUpdate> updates)
    {
        // Use a Redis pipeline (batch) to send all increments in one round trip.
        // Each HINCRBYFLOAT is individually atomic; the batch reduces latency.
        var batch = db.CreateBatch();
        var tasks = new List<Task>();

        tasks.Add(batch.HashDeleteAsync(key, EmptySnapshotField));

        foreach (var update in updates)
        {
            var field = MakeField(update);
            tasks.Add(batch.HashIncrementAsync(key, field, (double)update.Amount));
        }

        // Ensure the key has a TTL (set only if it doesn't already have one)
        tasks.Add(batch.KeyExpireAsync(key, DefaultKeyTtl, ExpireWhen.HasNoExpiry));

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    private static async Task MarkEmptySnapshotAsync(IDatabase db, RedisKey key)
    {
        var batch = db.CreateBatch();
        var setTask = batch.HashSetAsync(key, EmptySnapshotField, RedisValue.EmptyString);
        var expireTask = batch.KeyExpireAsync(key, DefaultKeyTtl);
        batch.Execute();
        await Task.WhenAll(setTask, expireTask);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — KEY / FIELD FORMATTING
    // ═══════════════════════════════════════════════════════════════════

    private RedisKey MakeKey(
        string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear)
    {
        var prefix = scope == AccumulatorScope.Family ? "FAM" : "IND";
        return $"accum:{_tenantContext.TenantId}:{prefix}:{ownerId}:{benefitPlanId}:{planYear}";
    }

    private static RedisValue MakeField(AccumulatorType type, NetworkTier tier)
        => $"{type}:{tier}";

    private static RedisValue MakeField(AccumulatorUpdate update)
    {
        // Visit/Day/Dollar counters use the source field for the service type code
        if (update.Type is AccumulatorType.VisitCount
            or AccumulatorType.DayCount
            or AccumulatorType.DollarLimit)
        {
            return update.Source; // Already formatted as "VisitCount:98" etc.
        }

        return $"{update.Type}:{update.NetworkTier}";
    }

    private static List<AccumulatorSnapshot> ParseSnapshots(
        HashEntry[] entries, AccumulatorScope scope)
    {
        var snapshots = new List<AccumulatorSnapshot>();

        foreach (var entry in entries)
        {
            var field = entry.Name.ToString();
            if (entry.Name == EmptySnapshotField) continue;

            var amount = (decimal)(double)entry.Value;

            // Parse "AccumulatorType:NetworkTier" field format
            var parts = field.Split(':', 2);
            if (parts.Length != 2) continue;

            if (!Enum.TryParse<AccumulatorType>(parts[0], out var type)) continue;
            if (!Enum.TryParse<NetworkTier>(parts[1], out var tier))
            {
                // Could be a service-type-code-keyed counter like "VisitCount:98"
                // These use InNetwork as a default tier since they're not network-specific
                tier = NetworkTier.InNetwork;
            }

            snapshots.Add(new AccumulatorSnapshot
            {
                Type = type,
                Scope = scope,
                NetworkTier = tier,
                LimitAmount = 0, // Limits come from BenefitPlanConfig, not the cache
                AccumulatedAmountAfter = amount
            });
        }

        return snapshots;
    }
}

// ═══════════════════════════════════════════════════════════════════
// SUPPORTING INTERFACES
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Provides runtime accumulator calculation from claim history.
/// This is the "source of truth" — Redis is just a cache on top.
///
/// Implemented by the host service (claims-service or benefit-plan-service)
/// by querying finalized claim lines and summing member responsibility
/// amounts by accumulator type.
///
/// For individual scope:
///   SELECT SUM(deductible_applied) as IndividualDeductible,
///          SUM(member_responsibility) as IndividualOopMax,
///          COUNT(*) as VisitCount  -- per service type code
///   FROM claim_lines
///   WHERE member_id = @memberId
///     AND plan_id = @planId
///     AND service_date BETWEEN @planYearStart AND @planYearEnd
///     AND status IN ('Finalized', 'Paid')
///
/// For family scope:
///   Same query but WHERE subscriber_id = @subscriberId
///   and summing across all family members.
/// </summary>
public interface IClaimsAccumulatorSource
{
    /// <summary>
    /// Calculates accumulator totals from claim history.
    /// </summary>
    /// <returns>
    /// A result where <c>Success</c> is <c>true</c> when the source was
    /// reachable and the data is authoritative (even if <c>Snapshots</c> is
    /// empty, meaning the owner has no accumulator history), and <c>false</c>
    /// when the source was unavailable or returned a non-success response —
    /// in which case the caller must NOT cache the result as an authoritative
    /// empty rebuild.
    /// </returns>
    Task<(bool Success, IReadOnlyList<AccumulatorSnapshot> Snapshots)> CalculateAccumulatorsAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);
}

/// <summary>
/// Optional: writes accumulator audit trail to durable storage.
/// The audit trail is non-blocking — adjudication does not wait for it.
/// Used for compliance reporting and portal display.
///
/// If not registered in DI, no audit trail is written.
/// The MongoDB/Cosmos AccumulatorDocument and ChoAccumulatorService
/// can be repurposed as the audit writer implementation.
/// </summary>
public interface IAccumulatorAuditWriter
{
    Task WriteAuditAsync(
        string tenantId, string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId, IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default);

    Task WriteReversalAuditAsync(
        string tenantId, string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default);
}
