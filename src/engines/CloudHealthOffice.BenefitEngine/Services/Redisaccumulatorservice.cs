using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Redis-backed accumulator service following the QNXT pattern: accumulators
/// are calculated at runtime from claim history, never stored as mutable state.
///
/// ── Architecture ────────────────────────────────────────────────────
///
///   Source of truth:  Finalized claim lines in claims-service
///   Hot cache:        Redis hash per member per plan year
///   Audit trail:      MongoDB AccumulatorDocument (written before the operation returns)
///
/// ── Why this approach ──────────────────────────────────────────────
///
/// Storing accumulator balances as mutable documents creates concurrency
/// problems: two claims for the same member adjudicating simultaneously
/// require optimistic concurrency, retry loops, and idempotency guards.
/// QNXT avoids this entirely by calculating accumulators at runtime.
///
/// Redis gives us the best of both worlds:
///   - HINCRBY (integer cents) is atomic — no read-modify-write race and no
///     floating-point drift
///   - HGETALL is sub-millisecond — within the 500ms adjudication target
///   - TTL handles annual reset — no batch delete job
///   - Cache miss → recalculate from claims → populate Redis
///
/// ── Redis key layout ──────────────────────────────────────────────
///
///   Hash key: "accum:{tenantId}:{memberId}:{planId}:{planYear}"
///   Fields:   "{AccumulatorType}:{NetworkTier}"  →  accumulated amount in
///             hundredths (cents for money, 1/100 for counts), an integer
///   Marker:   "__v" = "2" — the integer-cents schema. A hash without it was
///             written with HINCRBYFLOAT dollars; it is never read: the next
///             script drops its amounts and journals (keeping fences) and the
///             next read rebuilds it from claims-service.
///
///   Example:
///     accum:tenant-001:IND:M12345:plan-abc:2026
///       __v                                    → "2"
///       IndividualDeductible:InNetwork         → "75000"   ($750.00)
///       IndividualDeductible:OutOfNetwork      → "0"
///       IndividualOopMax:InNetwork             → "125000"  ($1,250.00)
///       VisitCount:98                          → "300"     (3 visits)
///
///   Family aggregates use subscriberId instead of memberId:
///     accum:tenant-001:FAM:S67890:plan-abc:2026
///       FamilyDeductible:InNetwork             → "150000"
///       FamilyOopMax:InNetwork                 → "210000"
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
/// When a claim is voided, the hash is invalidated and the next read
/// rebuilds from claim history (which no longer includes the voided claim).
/// </summary>
/// <remarks>
/// This service intentionally does NOT use the shared
/// <see cref="CloudHealthOffice.Infrastructure.Caching.ICacheProvider"/>
/// abstraction introduced in Addendum A.7.2. Accumulators use Redis hashes
/// with server-side atomic integer increments to avoid read-modify-write
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
            snapshots.Count, LogSanitizer.Sanitize(memberId), benefitPlanId, LogSanitizer.Sanitize(planYear));

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

        // The audit row is written first and awaited. These increments are not
        // idempotent, so a failure must stop the operation before anything is
        // added (the caller's retry then repeats the whole step); the audit
        // writer skips a claim it already recorded, so a retry after a failed
        // increment writes no second row.
        await WriteAuditOrThrowAsync(claimId, ct => _auditWriter!.WriteAuditAsync(
            _tenantContext.TenantId, memberId, subscriberId, benefitPlanId, planYear, claimId, updates, ct), ct);

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
            updates.Count, LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(memberId));
    }

    /// <summary>
    /// Writes an accumulator audit row (a compliance record) and waits for
    /// it. No audit writer registered: nothing to do. A failure is logged and
    /// rethrown as <see cref="AccumulatorAuditException"/>: the operation
    /// fails and its caller retries it (every audited operation here is safe
    /// to repeat, and the writer is idempotent per claim), so an audit row is
    /// never dropped — not on a write error, and not on shutdown, as a
    /// detached background write could be.
    /// </summary>
    private async Task WriteAuditOrThrowAsync(string claimId, Func<CancellationToken, Task> write, CancellationToken ct)
    {
        if (_auditWriter is null) return;
        try
        {
            await write(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Accumulator audit write failed for claim {ClaimId}; the operation fails and must be retried",
                LogSanitizer.Sanitize(claimId));
            throw new AccumulatorAuditException(claimId, ex);
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

        await InvalidateAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, fence: false);

        // The invalidation is idempotent, so the audit follows it; a failure fails the reversal.
        await WriteAuditOrThrowAsync(claimId, ct => _auditWriter!.WriteReversalAuditAsync(
            _tenantContext.TenantId, memberId, subscriberId, benefitPlanId, planYear, claimId, ct), ct);
    }

    /// <summary>
    /// <see cref="ReverseAsync"/> for a voided or denied claim: the cache is
    /// invalidated the same way (the rebuild from claims-service counts only
    /// Approved / Paid claims, so it no longer includes this one), and the
    /// claim id is fenced in each hash (<c>__fence:{claimId}</c>), in the same
    /// script — a <see cref="CommitAsync"/> of it that arrives later is refused.
    /// </summary>
    /// <summary>
    /// Commit-scoped, non-terminal reversal (see the interface): per hash, a
    /// newer journalled commit of the claim makes it Superseded (nothing
    /// changes); otherwise the hash is invalidated as by
    /// <see cref="ReverseAsync"/> and the commit id fenced
    /// (<c>__fence:commit:{commitId}</c>, kept by invalidations like every
    /// fence), in the same script. A journal written by a rebuild names no
    /// commit, so it cannot be compared: that hash is invalidated (always
    /// safe — the rebuild counts the claim from claims-service's status).
    /// </summary>
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
        var db = _redis.GetDatabase();
        var args = new RedisValue[] { claimId, commitId, (long)DefaultKeyTtl.TotalSeconds, NewGeneration() };
        var outcomes = await Task.WhenAll(
            db.ScriptEvaluateAsync(ReverseCommitScript,
                new RedisKey[] { MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear) }, args),
            db.ScriptEvaluateAsync(ReverseCommitScript,
                new RedisKey[] { MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear) }, args));
        var superseded = outcomes.Any(o => o.ToString() == "SUPERSEDED");
        _logger.LogInformation(
            "Commit-scoped reversal of commit {CommitId} for claim {ClaimId}: {Outcome}",
            LogSanitizer.Sanitize(commitId), LogSanitizer.Sanitize(claimId), superseded ? "superseded" : "reversed");
        return superseded ? AccumulatorReversalOutcome.Superseded : AccumulatorReversalOutcome.Reversed;
    }

    /// <summary>KEYS[1] the hash. ARGV: claim id, commit id, TTL seconds, a fresh generation. Returns SUPERSEDED or REVERSED.</summary>
    private const string ReverseCommitScript = @"
local key = KEYS[1]
local own = redis.call('HGET', key, '__commit:' .. ARGV[1])
if own then
  local sep = string.find(own, '|', 1, true)
  local applied = own
  if sep then applied = string.sub(own, 1, sep - 1) end
  if applied ~= ARGV[2] and applied ~= 'rebuild' then return 'SUPERSEDED' end
end
local fences = {}
for _, f in ipairs(redis.call('HKEYS', key)) do
  if string.sub(f, 1, 8) == '__fence:' then table.insert(fences, f) end
end
table.insert(fences, '__fence:commit:' .. ARGV[2])
redis.call('DEL', key)
for _, f in ipairs(fences) do redis.call('HSET', key, f, '1') end
redis.call('HSET', key, '__gen', ARGV[4])
redis.call('EXPIRE', key, ARGV[3])
return 'REVERSED'
";

    public async Task ReverseTerminallyAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        await InvalidateAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, fence: true);
        await WriteAuditOrThrowAsync(claimId, ct => _auditWriter!.WriteReversalAuditAsync(
            _tenantContext.TenantId, memberId, subscriberId, benefitPlanId, planYear, claimId, ct), ct);
    }

    /// <summary>
    /// Writes a prepared commit, one Lua script per hash (individual, family),
    /// so the checks and the increments are one atomic step on a single key
    /// (cluster-safe):
    /// <list type="bullet">
    ///   <item>refused when the hash fences the claim (<c>__fence:{claimId}</c>);</item>
    ///   <item>a no-op when this commit id already wrote it;</item>
    ///   <item>otherwise what the claim's own earlier write and the replaced
    ///     claim's write put in the hash is taken back, and the new amounts
    ///     added — every money cap (deductible, OOP maximum, ACA cap) no
    ///     further than its limit as it stands now; what was trimmed is
    ///     returned.</item>
    /// </list>
    /// Every claim's contribution is journalled in the hash
    /// (<c>__commit:{claimId}</c> = commit id and, per field, the amount in
    /// the hash with its type, tier and source), written by commits and by
    /// the rebuild alike, so a later commit can undo exactly what is there and
    /// pricing can take a claim's own earlier write out
    /// (<see cref="GetClaimUpdatesAsync"/>). A hash holding no amounts
    /// (evicted or invalidated) is not incremented: the next read rebuilds it
    /// from claims-service, which counts the claim (it is Approved before it
    /// commits) and records its contribution.
    /// </summary>
    public async Task<AccumulatorCommitResult> CommitAsync(AccumulatorCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (string.IsNullOrWhiteSpace(commit.ClaimId))
            throw new ArgumentException("The commit names no claim.", nameof(commit));
        var db = _redis.GetDatabase();
        var replaced = string.IsNullOrWhiteSpace(commit.ReplacesClaimId)
                       || string.Equals(commit.ReplacesClaimId, commit.ClaimId, StringComparison.Ordinal)
            ? string.Empty
            : commit.ReplacesClaimId!;

        async Task<(string Outcome, List<AccumulatorClamp> Clamped)> CommitKeyAsync(RedisKey key, List<AccumulatorUpdate> updates)
        {
            var args = new List<RedisValue>
            {
                commit.ClaimId, commit.CommitId, replaced, (long)DefaultKeyTtl.TotalSeconds, NewGeneration(),
            };
            foreach (var u in updates)
            {
                args.Add(MakeField(u));
                args.Add(AccumulatorCents.ToCents(u.Amount));
                args.Add(u.ClampAtLimit is decimal limit ? AccumulatorCents.ToCents(limit) : string.Empty);
                args.Add(u.Type.ToString());
                args.Add(u.NetworkTier.ToString());
                args.Add(u.Source ?? string.Empty);
            }
            var raw = (await db.ScriptEvaluateAsync(CommitScript, new[] { key }, args.ToArray())).ToString();
            var sep = raw.IndexOf('|');
            var outcome = sep < 0 ? raw : raw[..sep];
            var clamped = new List<AccumulatorClamp>();
            if (sep >= 0 && raw.Length > sep + 1)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw[(sep + 1)..]);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var c in doc.RootElement.EnumerateArray())
                    {
                        var field = c.GetProperty("f").GetString();
                        var update = updates.FirstOrDefault(u => MakeField(u) == field);
                        if (update is null) continue;
                        clamped.Add(new AccumulatorClamp
                        {
                            Type = update.Type, Scope = update.Scope, NetworkTier = update.NetworkTier,
                            Requested = AccumulatorCents.FromJson(c.GetProperty("r")),
                            Applied = AccumulatorCents.FromJson(c.GetProperty("a")),
                            Limit = update.ClampAtLimit ?? 0m,
                        });
                    }
                }
            }
            return (outcome, clamped);
        }

        var outcomes = await Task.WhenAll(
            CommitKeyAsync(MakeKey(commit.MemberId, AccumulatorScope.Individual, commit.BenefitPlanId, commit.PlanYear),
                commit.Updates.Where(u => u.Scope == AccumulatorScope.Individual).ToList()),
            CommitKeyAsync(MakeKey(commit.SubscriberId, AccumulatorScope.Family, commit.BenefitPlanId, commit.PlanYear),
                commit.Updates.Where(u => u.Scope == AccumulatorScope.Family).ToList()));

        if (outcomes.Any(o => o.Outcome == "REFUSED_COMMIT"))
        {
            _logger.LogInformation(
                "Commit {CommitId} for claim {ClaimId} refused: that commit was reversed (the claim was denied while it was in flight)",
                LogSanitizer.Sanitize(commit.CommitId), LogSanitizer.Sanitize(commit.ClaimId));
            return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedCommitReversed);
        }
        if (outcomes.Any(o => o.Outcome == "REFUSED"))
        {
            _logger.LogWarning(
                "Commit {CommitId} for claim {ClaimId} refused: the claim was reversed terminally (void or denial)",
                LogSanitizer.Sanitize(commit.CommitId), LogSanitizer.Sanitize(commit.ClaimId));
            return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedClaimReversed);
        }
        if (outcomes.All(o => o.Outcome == "ALREADY"))
        {
            // A repeat: the clamps the first attempt recorded come back too.
            // The audit is written again (idempotent per claim): a first attempt
            // whose audit write failed is completed by this retry.
            if (commit.Updates.Count > 0)
            {
                await WriteAuditOrThrowAsync(commit.ClaimId, ct => _auditWriter!.WriteAuditAsync(
                    _tenantContext.TenantId, commit.MemberId, commit.SubscriberId,
                    commit.BenefitPlanId, commit.PlanYear, commit.ClaimId, commit.Updates, ct), ct);
            }
            return new AccumulatorCommitResult
            {
                Outcome = AccumulatorCommitOutcome.AlreadyCommitted,
                Clamped = outcomes.SelectMany(o => o.Clamped).ToList(),
            };
        }

        // Awaited: a failure fails the commit, and its retry (the claims-service
        // accumulator outbox) comes back ALREADY and writes the audit then.
        if (commit.Updates.Count > 0)
        {
            await WriteAuditOrThrowAsync(commit.ClaimId, ct => _auditWriter!.WriteAuditAsync(
                _tenantContext.TenantId, commit.MemberId, commit.SubscriberId,
                commit.BenefitPlanId, commit.PlanYear, commit.ClaimId, commit.Updates, ct), ct);
        }
        return new AccumulatorCommitResult
        {
            Outcome = AccumulatorCommitOutcome.Committed,
            Clamped = outcomes.SelectMany(o => o.Clamped).ToList(),
        };
    }

    /// <summary>
    /// The claim's contribution as journalled in its hashes (written by its
    /// commit, or by the rebuild that counted it): pricing a re-adjudication
    /// or a replacement takes it out of the starting balances. Nothing for a
    /// claim whose contribution is not in the cache.
    /// </summary>
    public async Task<IReadOnlyList<AccumulatorUpdate>> GetClaimUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var updates = new List<AccumulatorUpdate>();
        foreach (var (owner, scope) in new[] { (memberId, AccumulatorScope.Individual), (subscriberId, AccumulatorScope.Family) })
        {
            var values = await db.HashGetAsync(MakeKey(owner, scope, benefitPlanId, planYear),
                new RedisValue[] { SchemaVersionField, CommitFieldPrefix + claimId });
            // A journal in a hash written before integer cents holds dollars: not read.
            if (values[0] != SchemaVersion || values[1].IsNullOrEmpty) continue;
            updates.AddRange(ParseJournal(values[1].ToString(), scope));
        }
        return updates;
    }

    /// <summary>
    /// A commit record (its own commit, or the rebuild that counted it) in
    /// either hash. A hash rebuilt or evicted since holds a record only for
    /// claims claims-service counts with non-zero amounts.
    /// </summary>
    public async Task<bool> HasClaimRecordAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var field = CommitFieldPrefix + claimId;
        return await db.HashExistsAsync(MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear), field)
               || await db.HashExistsAsync(MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear), field);
    }

    private static IEnumerable<AccumulatorUpdate> ParseJournal(string record, AccumulatorScope scope)
    {
        var sep = record.IndexOf('|');
        if (sep < 0) yield break;
        using var doc = System.Text.Json.JsonDocument.Parse(record[(sep + 1)..]);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;
        var parsed = new List<AccumulatorUpdate>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var amount = AccumulatorCents.FromJson(entry.Value.GetProperty("a"));
            if (amount <= 0) continue;
            if (!Enum.TryParse<AccumulatorType>(entry.Value.GetProperty("t").GetString(), out var type)) continue;
            if (!Enum.TryParse<NetworkTier>(entry.Value.GetProperty("n").GetString(), out var tier)) tier = NetworkTier.InNetwork;
            parsed.Add(new AccumulatorUpdate
            {
                Type = type, Scope = scope, NetworkTier = tier, Amount = amount,
                Source = entry.Value.TryGetProperty("s", out var s) && s.GetString() is { Length: > 0 } src ? src : entry.Name,
            });
        }
        foreach (var u in parsed) yield return u;
    }

    /// <summary>Marker fields in an accumulator hash; never amounts.</summary>
    internal const string CommitFieldPrefix = "__commit:";
    internal const string FenceFieldPrefix = "__fence:";

    /// <summary>
    /// The hash's generation: a fresh token written by every change a
    /// rebuild in flight must not miss — a commit that lands on a cold hash
    /// (it adds nothing and relies on the rebuild) and an invalidation. A
    /// rebuild reads it before it asks claims-service and populates only if
    /// it is unchanged; otherwise it asks again (<see cref="GetOrRebuildAsync"/>).
    /// </summary>
    internal const string GenerationField = "__gen";

    /// <summary>
    /// Schema marker: <c>__v</c> = <see cref="SchemaVersion"/> on every hash
    /// written with integer cents. A hash without it holds HINCRBYFLOAT
    /// dollars and is treated as cold (see the type's remarks).
    /// </summary>
    internal const string SchemaVersionField = "__v";
    internal const string SchemaVersion = "2";

    private static string NewGeneration() => Guid.NewGuid().ToString("N");

    private static bool IsMarkerField(RedisValue name)
    {
        var field = name.ToString();
        return field.StartsWith(CommitFieldPrefix, StringComparison.Ordinal)
               || field.StartsWith(FenceFieldPrefix, StringComparison.Ordinal)
               || field == GenerationField
               || field == SchemaVersionField;
    }

    /// <summary>Shared Lua: is the hash holding amounts; undo a claim's journalled contribution.</summary>
    private const string JournalLua = @"
local function populated(key)
  if redis.call('HGET', key, '__v') ~= '2' then return false end
  for _, f in ipairs(redis.call('HKEYS', key)) do
    if string.sub(f, 1, 9) ~= '__commit:' and string.sub(f, 1, 8) ~= '__fence:' and f ~= '__gen' and f ~= '__v' then return true end
  end
  return false
end
-- A hash written before integer cents (no __v = 2): drop its amounts and
-- commit journals (dollars), keep its fences and generation. It is then cold
-- and the next read rebuilds it from claims-service. True when it dropped any.
local function dropLegacy(key)
  if redis.call('HGET', key, '__v') == '2' then return false end
  local drop = {}
  for _, f in ipairs(redis.call('HKEYS', key)) do
    if string.sub(f, 1, 8) ~= '__fence:' and f ~= '__gen' then table.insert(drop, f) end
  end
  for _, f in ipairs(drop) do redis.call('HDEL', key, f) end
  return #drop > 0
end
local function clampsOf(journal)
  local clamps = {}
  for f, e in pairs(journal) do
    if type(e) == 'table' and e.r then table.insert(clamps, { f = f, r = e.r, a = e.a }) end
  end
  return clamps
end
local function amountOf(v)
  if type(v) == 'table' then return tonumber(v.a) or 0 end
  return tonumber(v) or 0
end
";

    /// <summary>
    /// KEYS[1] the hash. ARGV: claim id, commit id, replaced claim id (or
    /// empty), TTL seconds, a fresh generation token, then (field, amount,
    /// limit-or-empty, type, tier, source) sextuples, amounts and limits in
    /// integer cents. Returns REFUSED,
    /// ALREADY|[clamps] (what the first attempt clamped) or COMMITTED|[clamps].
    /// A replacement fences the claim it replaces (superseded for good: a
    /// late commit of it is refused). On a cold hash nothing is added and
    /// the generation changes, so a rebuild in flight asks claims-service
    /// again (it counts this claim: it is Approved before it commits).
    /// </summary>
    private const string CommitScript = JournalLua + @"
local key = KEYS[1]
local claim = ARGV[1]
local commitId = ARGV[2]
if redis.call('HEXISTS', key, '__fence:' .. claim) == 1 then return 'REFUSED' end
if redis.call('HEXISTS', key, '__fence:commit:' .. commitId) == 1 then return 'REFUSED_COMMIT' end
dropLegacy(key)
redis.call('HSET', key, '__v', '2')
local own = redis.call('HGET', key, '__commit:' .. claim)
if own then
  local sep = string.find(own, '|', 1, true)
  if sep and string.sub(own, 1, sep - 1) == commitId then
    local ok, j = pcall(cjson.decode, string.sub(own, sep + 1))
    if ok and type(j) == 'table' then
      local c = clampsOf(j)
      if #c > 0 then return 'ALREADY|' .. cjson.encode(c) end
    end
    return 'ALREADY'
  end
end
local hot = populated(key)
if not hot then redis.call('HSET', key, '__gen', ARGV[5]) end
local function undo(c)
  local v = redis.call('HGET', key, '__commit:' .. c)
  if not v then return end
  local sep = string.find(v, '|', 1, true)
  if sep and hot then
    local added = cjson.decode(string.sub(v, sep + 1))
    for f, e in pairs(added) do
      local cur = tonumber(redis.call('HGET', key, f) or '0') or 0
      local nv = cur - amountOf(e)
      if nv < 0 then nv = 0 end
      redis.call('HSET', key, f, string.format('%d', nv))
    end
  end
  redis.call('HDEL', key, '__commit:' .. c)
end
undo(claim)
if ARGV[3] ~= '' then
  undo(ARGV[3])
  redis.call('HSET', key, '__fence:' .. ARGV[3], '1')
end
local journal = {}
local clamps = {}
local any = false
local i = 6
while i <= #ARGV do
  local f = ARGV[i]
  local a = tonumber(ARGV[i + 1]) or 0
  local lim = ARGV[i + 2]
  if hot then
    local requested = a
    local cur = tonumber(redis.call('HGET', key, f) or '0') or 0
    if lim ~= '' and a > 0 then
      local room = (tonumber(lim) or 0) - cur
      if room < 0 then room = 0 end
      if a > room then a = room end
    end
    if a ~= requested then table.insert(clamps, { f = f, r = requested, a = a }) end
    redis.call('HSET', key, f, string.format('%d', cur + a))
    local e = journal[f]
    if e then e.a = e.a + a else journal[f] = { a = a, t = ARGV[i + 3], n = ARGV[i + 4], s = ARGV[i + 5] } end
    if a ~= requested then journal[f].r = (journal[f].r or 0) + requested end
    any = true
  end
  i = i + 6
end
if any then redis.call('HDEL', key, '__empty') end
local encoded = '{}'
if any then encoded = cjson.encode(journal) end
redis.call('HSET', key, '__commit:' .. claim, commitId .. '|' .. encoded)
if redis.call('TTL', key) < 0 then redis.call('EXPIRE', key, ARGV[4]) end
if #clamps > 0 then return 'COMMITTED|' .. cjson.encode(clamps) end
return 'COMMITTED'
";

    /// <summary>
    /// KEYS[1] the hash. ARGV[1] TTL seconds, ARGV[2] the totals as JSON
    /// (field → integer cents; empty object = authoritatively empty), ARGV[3] the
    /// per-claim contributions as JSON (claim id → journal). Populates a hash
    /// holding no amounts — totals plus one journal record per counted claim
    /// (keeping the commit id of a commit that landed while the hash was cold,
    /// so its repeat stays a no-op) — and leaves a hash another writer already
    /// populated alone. Fences are kept. ARGV[4] is the generation the
    /// rebuild read before asking claims-service ('' for none): if a cold
    /// commit or an invalidation changed it meanwhile, nothing is written and
    /// STALE is returned (the caller asks again). Returns POPULATED, SKIPPED
    /// or STALE.
    /// </summary>
    private const string RebuildScript = JournalLua + @"
local key = KEYS[1]
if populated(key) then return 'SKIPPED' end
local gen = redis.call('HGET', key, '__gen') or ''
if gen ~= ARGV[4] then return 'STALE' end
dropLegacy(key)
redis.call('HSET', key, '__v', '2')
local totals = cjson.decode(ARGV[2])
local claims = cjson.decode(ARGV[3])
local anyTotal = false
for f, a in pairs(totals) do
  redis.call('HSET', key, f, string.format('%d', a))
  anyTotal = true
end
if not anyTotal then redis.call('HSET', key, '__empty', '') end
for c, journal in pairs(claims) do
  if redis.call('HEXISTS', key, '__fence:' .. c) == 0 then
    local commitId = 'rebuild'
    local existing = redis.call('HGET', key, '__commit:' .. c)
    if existing then
      local sep = string.find(existing, '|', 1, true)
      if sep then commitId = string.sub(existing, 1, sep - 1) end
    end
    redis.call('HSET', key, '__commit:' .. c, commitId .. '|' .. cjson.encode(journal))
  end
end
redis.call('EXPIRE', key, ARGV[1])
return 'POPULATED'
";

    /// <summary>
    /// KEYS[1] the hash. ARGV: claim id, '1' to fence it, TTL seconds, a
    /// fresh generation token. Drops every amount and commit record (the
    /// next read rebuilds from claims-service), keeps the fences, adding
    /// this claim's when asked, and changes the generation so a rebuild that
    /// read claims-service before the change does not populate the hash.
    /// </summary>
    private const string InvalidateScript = @"
local key = KEYS[1]
local fences = {}
for _, f in ipairs(redis.call('HKEYS', key)) do
  if string.sub(f, 1, 8) == '__fence:' then table.insert(fences, f) end
end
if ARGV[2] == '1' then table.insert(fences, '__fence:' .. ARGV[1]) end
redis.call('DEL', key)
for _, f in ipairs(fences) do redis.call('HSET', key, f, '1') end
redis.call('HSET', key, '__gen', ARGV[4])
redis.call('EXPIRE', key, ARGV[3])
return #fences
";

    private async Task InvalidateAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear, string claimId, bool fence)
    {
        var db = _redis.GetDatabase();
        var args = new RedisValue[] { claimId, fence ? "1" : "0", (long)DefaultKeyTtl.TotalSeconds, NewGeneration() };
        await Task.WhenAll(
            db.ScriptEvaluateAsync(InvalidateScript,
                new RedisKey[] { MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear) }, args),
            db.ScriptEvaluateAsync(InvalidateScript,
                new RedisKey[] { MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear) }, args));

        _logger.LogInformation(
            "Invalidated accumulator cache for claim reversal {ClaimId} (member {MemberId}, fenced: {Fenced})",
            LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(memberId), fence);
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
        // The audit trail in MongoDB is retained for compliance.

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

        // Try Redis first. Marker fields (commit records, fences) are not
        // amounts: a hash holding only those is a miss. So is a hash written
        // before integer cents (no schema marker): its dollars are never read.
        var entries = AmountEntries(await db.HashGetAllAsync(key));

        if (entries.Length > 0)
        {
            return ParseSnapshots(entries, scope);
        }

        // Cache miss — rebuild from claim history
        _logger.LogDebug(
            "Cache miss for {Key}. Rebuilding from claim history.", key.ToString());

        for (var attempt = 1; ; attempt++)
        {
            // The generation before claims-service is asked: a commit landing
            // on the cold hash (or an invalidation) while it answers changes
            // it, and the populate below refuses to write a stale answer.
            var generation = (string?)await db.HashGetAsync(key, GenerationField) ?? string.Empty;
            var (outcome, snapshots) = await RebuildOnceAsync(db, key, ownerId, scope, benefitPlanId, planYear, generation, ct);
            if (outcome != "STALE") return snapshots;
            if (attempt >= MaxRebuildAttempts)
            {
                // Still racing: serve this answer, cache nothing; the next read rebuilds.
                _logger.LogWarning(
                    "Accumulator rebuild for {Key} raced {Attempts} concurrent changes; serving uncached", key.ToString(), attempt);
                return snapshots;
            }
        }
    }

    private const int MaxRebuildAttempts = 3;

    private async Task<(string Outcome, List<AccumulatorSnapshot> Snapshots)> RebuildOnceAsync(
        IDatabase db, RedisKey key, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear, string generation, CancellationToken ct)
    {
        var rebuild = await _claimsSource.CalculateAccumulatorsWithClaimsAsync(
            _tenantContext.TenantId, ownerId, scope, benefitPlanId, planYear, ct);

        if (!rebuild.Success)
        {
            // Source was unavailable — do not cache; the next read will retry.
            _logger.LogWarning(
                "Accumulator source unavailable for {Key}. Skipping Redis cache population.",
                key.ToString());
            return ("UNAVAILABLE", []);
        }

        // Populate Redis atomically with one journal record per counted claim,
        // so a later commit can undo exactly what the rebuild put in for it
        // (a replacement taking its predecessor back, a re-commit of the same
        // claim) and pricing can exclude a claim's own contribution.
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var snapshot in rebuild.Snapshots)
        {
            var field = MakeField(snapshot.Type, snapshot.NetworkTier).ToString();
            totals[field] = (totals.TryGetValue(field, out var t) ? t : 0L) + AccumulatorCents.ToCents(snapshot.AccumulatedAmountAfter);
        }
        var claims = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var claim in rebuild.Claims)
        {
            var journal = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var u in claim.Updates.Where(u => u.Amount > 0))
            {
                var field = MakeField(u).ToString();
                var amount = AccumulatorCents.ToCents(u.Amount) + (journal.TryGetValue(field, out var e) ? ((JournalEntry)e).a : 0L);
                journal[field] = new JournalEntry(amount, u.Type.ToString(), u.NetworkTier.ToString(), u.Source ?? string.Empty);
            }
            if (journal.Count > 0) claims[claim.ClaimId] = journal;
        }

        var outcome = (await db.ScriptEvaluateAsync(RebuildScript, new[] { key }, new RedisValue[]
        {
            (long)DefaultKeyTtl.TotalSeconds,
            System.Text.Json.JsonSerializer.Serialize(totals),
            claims.Count == 0 ? "{}" : System.Text.Json.JsonSerializer.Serialize(claims),
            generation,
        })).ToString();

        if (outcome == "SKIPPED")
        {
            // Another writer populated it meanwhile: read what is there.
            var now = AmountEntries(await db.HashGetAllAsync(key));
            return (outcome, ParseSnapshots(now, scope));
        }
        if (outcome == "STALE")
        {
            _logger.LogDebug("Accumulator rebuild for {Key} is stale (the hash changed while claims-service answered); asking again", key.ToString());
            return (outcome, rebuild.Snapshots.ToList());
        }

        _logger.LogDebug(
            "Rebuilt {Count} accumulators ({Claims} claim record(s)) for {Key} from claim history.",
            rebuild.Snapshots.Count, claims.Count, key.ToString());

        return (outcome, rebuild.Snapshots.ToList());
    }

    /// <summary>One journalled field, <c>a</c> in integer cents (Lua reads <c>a</c>, <c>t</c>, <c>n</c>, <c>s</c>).</summary>
    private sealed record JournalEntry(long a, string t, string n, string s);

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE — ATOMIC INCREMENTS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ApplyIncrementsAsync(
        IDatabase db, RedisKey key, List<AccumulatorUpdate> updates)
    {
        // One script: integer-cent HINCRBYs, atomic together. A hash written
        // before integer cents is not added to (its dollars are dropped and the
        // next read rebuilds it from claims-service).
        var args = new List<RedisValue> { (long)DefaultKeyTtl.TotalSeconds, NewGeneration() };
        foreach (var update in updates)
        {
            args.Add(MakeField(update));
            args.Add(AccumulatorCents.ToCents(update.Amount));
        }

        var outcome = (await db.ScriptEvaluateAsync(ApplyScript, new[] { key }, args.ToArray())).ToString();
        if (outcome == "LEGACY")
        {
            _logger.LogWarning(
                "Accumulator hash {Key} predates integer cents: its amounts were dropped and the next read rebuilds it from claims-service; {Count} increment(s) not applied to it",
                key.ToString(), updates.Count);
        }
    }

    /// <summary>
    /// KEYS[1] the hash. ARGV[1] TTL seconds, ARGV[2] a fresh generation
    /// token, then (field, integer cents) pairs. Returns APPLIED, or LEGACY
    /// for a hash written before integer cents (dropped, nothing added).
    /// </summary>
    private const string ApplyScript = JournalLua + @"
local key = KEYS[1]
if dropLegacy(key) then
  redis.call('HSET', key, '__gen', ARGV[2])
  return 'LEGACY'
end
redis.call('HSET', key, '__v', '2')
redis.call('HDEL', key, '__empty')
local i = 3
while i <= #ARGV do
  redis.call('HINCRBY', key, ARGV[i], ARGV[i + 1])
  i = i + 2
end
if redis.call('TTL', key) < 0 then redis.call('EXPIRE', key, ARGV[1]) end
return 'APPLIED'
";

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

    /// <summary>The amount fields of a hash, or none when it predates integer cents.</summary>
    private static HashEntry[] AmountEntries(HashEntry[] all) =>
        all.Any(e => e.Name == SchemaVersionField && e.Value == SchemaVersion)
            ? all.Where(e => !IsMarkerField(e.Name)).ToArray()
            : [];

    private static List<AccumulatorSnapshot> ParseSnapshots(
        HashEntry[] entries, AccumulatorScope scope)
    {
        var snapshots = new List<AccumulatorSnapshot>();

        foreach (var entry in entries)
        {
            var field = entry.Name.ToString();
            if (entry.Name == EmptySnapshotField) continue;

            var amount = AccumulatorCents.FromRedis(entry.Value, field);

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

    /// <summary>
    /// <see cref="CalculateAccumulatorsAsync"/> plus each counted claim's own
    /// contribution, so the rebuilt cache journals it per claim (a later
    /// commit then undoes exactly that). The default returns no per-claim
    /// contributions (a claims-service without them): the cache is rebuilt
    /// without journal records.
    /// </summary>
    async Task<AccumulatorRebuildData> CalculateAccumulatorsWithClaimsAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var (success, snapshots) = await CalculateAccumulatorsAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct);
        return new AccumulatorRebuildData(success, snapshots, []);
    }
}

/// <summary>A cache rebuild: the totals and each counted claim's contribution.</summary>
public sealed record AccumulatorRebuildData(
    bool Success,
    IReadOnlyList<AccumulatorSnapshot> Snapshots,
    IReadOnlyList<ClaimAccumulatorContribution> Claims);

/// <summary>What one finalized claim contributes to an owner's accumulators.</summary>
public sealed record ClaimAccumulatorContribution(string ClaimId, IReadOnlyList<AccumulatorUpdate> Updates);

/// <summary>
/// Optional: writes accumulator audit trail to durable storage.
/// Every write is awaited by the operation it records; a failure fails that
/// operation, which its caller retries, so implementations must be idempotent
/// per claim. Used for compliance reporting and portal display.
///
/// If not registered in DI, no audit trail is written.
/// The MongoDB AccumulatorDocument and ChoAccumulatorService
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

/// <summary>An accumulator audit row could not be written; the operation it records must be retried.</summary>
public sealed class AccumulatorAuditException : Exception
{
    public AccumulatorAuditException(string claimId, Exception inner)
        : base($"The accumulator audit row for claim {claimId} could not be written; retry the operation.", inner)
    {
        ClaimId = claimId;
    }

    public string ClaimId { get; }
}
