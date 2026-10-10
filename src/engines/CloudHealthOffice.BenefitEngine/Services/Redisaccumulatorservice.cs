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

        await InvalidateAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, fence: false);

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

    /// <summary>
    /// <see cref="ReverseAsync"/> for a voided or denied claim: the cache is
    /// invalidated the same way (the rebuild from claims-service counts only
    /// Approved / Paid claims, so it no longer includes this one), and the
    /// claim id is fenced in each hash (<c>__fence:{claimId}</c>), in the same
    /// script — a <see cref="CommitAsync"/> of it that arrives later is refused.
    /// </summary>
    public async Task ReverseTerminallyAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
    {
        await InvalidateAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, fence: true);
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
                        "Failed to write reversal audit for claim {ClaimId}", SanitizeForLog(claimId));
                }
            }, ct);
        }
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
                commit.ClaimId, commit.CommitId, replaced, (long)DefaultKeyTtl.TotalSeconds,
            };
            foreach (var u in updates)
            {
                args.Add(MakeField(u));
                args.Add(Num(u.Amount));
                args.Add(u.ClampAtLimit is decimal limit ? Num(limit) : string.Empty);
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
                            Requested = (decimal)c.GetProperty("r").GetDouble(),
                            Applied = (decimal)c.GetProperty("a").GetDouble(),
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

        if (outcomes.Any(o => o.Outcome == "REFUSED"))
        {
            _logger.LogWarning(
                "Commit {CommitId} for claim {ClaimId} refused: the claim was reversed terminally (void or denial)",
                SanitizeForLog(commit.CommitId), SanitizeForLog(commit.ClaimId));
            return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.RefusedClaimReversed);
        }
        if (outcomes.All(o => o.Outcome == "ALREADY")) return AccumulatorCommitResult.Of(AccumulatorCommitOutcome.AlreadyCommitted);

        if (_auditWriter is not null && commit.Updates.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _auditWriter.WriteAuditAsync(
                        _tenantContext.TenantId, commit.MemberId, commit.SubscriberId,
                        commit.BenefitPlanId, commit.PlanYear, commit.ClaimId, commit.Updates, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to write accumulator audit for claim {ClaimId}", SanitizeForLog(commit.ClaimId));
                }
            }, ct);
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
            var record = await db.HashGetAsync(MakeKey(owner, scope, benefitPlanId, planYear), CommitFieldPrefix + claimId);
            if (record.IsNullOrEmpty) continue;
            updates.AddRange(ParseJournal(record.ToString(), scope));
        }
        return updates;
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
            var amount = (decimal)entry.Value.GetProperty("a").GetDouble();
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

    private static string Num(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Marker fields in an accumulator hash; never amounts.</summary>
    internal const string CommitFieldPrefix = "__commit:";
    internal const string FenceFieldPrefix = "__fence:";

    private static bool IsMarkerField(RedisValue name)
    {
        var field = name.ToString();
        return field.StartsWith(CommitFieldPrefix, StringComparison.Ordinal)
               || field.StartsWith(FenceFieldPrefix, StringComparison.Ordinal);
    }

    /// <summary>Shared Lua: is the hash holding amounts; undo a claim's journalled contribution.</summary>
    private const string JournalLua = @"
local function populated(key)
  for _, f in ipairs(redis.call('HKEYS', key)) do
    if string.sub(f, 1, 9) ~= '__commit:' and string.sub(f, 1, 8) ~= '__fence:' then return true end
  end
  return false
end
local function amountOf(v)
  if type(v) == 'table' then return tonumber(v.a) or 0 end
  return tonumber(v) or 0
end
";

    /// <summary>
    /// KEYS[1] the hash. ARGV: claim id, commit id, replaced claim id (or
    /// empty), TTL seconds, then (field, amount, limit-or-empty, type, tier,
    /// source) sextuples. Returns REFUSED, ALREADY or COMMITTED|[clamps].
    /// </summary>
    private const string CommitScript = JournalLua + @"
local key = KEYS[1]
local claim = ARGV[1]
local commitId = ARGV[2]
if redis.call('HEXISTS', key, '__fence:' .. claim) == 1 then return 'REFUSED' end
local own = redis.call('HGET', key, '__commit:' .. claim)
if own then
  local sep = string.find(own, '|', 1, true)
  if sep and string.sub(own, 1, sep - 1) == commitId then return 'ALREADY' end
end
local hot = populated(key)
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
      redis.call('HSET', key, f, tostring(nv))
    end
  end
  redis.call('HDEL', key, '__commit:' .. c)
end
undo(claim)
if ARGV[3] ~= '' then undo(ARGV[3]) end
local journal = {}
local clamps = {}
local any = false
local i = 5
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
    redis.call('HSET', key, f, tostring(cur + a))
    local e = journal[f]
    if e then e.a = e.a + a else journal[f] = { a = a, t = ARGV[i + 3], n = ARGV[i + 4], s = ARGV[i + 5] } end
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
    /// (field → amount; empty object = authoritatively empty), ARGV[3] the
    /// per-claim contributions as JSON (claim id → journal). Populates a hash
    /// holding no amounts — totals plus one journal record per counted claim
    /// (keeping the commit id of a commit that landed while the hash was cold,
    /// so its repeat stays a no-op) — and leaves a hash another writer already
    /// populated alone. Fences are kept. Returns POPULATED or SKIPPED.
    /// </summary>
    private const string RebuildScript = JournalLua + @"
local key = KEYS[1]
if populated(key) then return 'SKIPPED' end
local totals = cjson.decode(ARGV[2])
local claims = cjson.decode(ARGV[3])
local anyTotal = false
for f, a in pairs(totals) do
  redis.call('HSET', key, f, tostring(a))
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
    /// KEYS[1] the hash. ARGV: claim id, '1' to fence it. Drops every amount
    /// and commit record (the next read rebuilds from claims-service) and
    /// keeps the fences, adding this claim's when asked.
    /// </summary>
    private const string InvalidateScript = @"
local key = KEYS[1]
local fences = {}
for _, f in ipairs(redis.call('HKEYS', key)) do
  if string.sub(f, 1, 8) == '__fence:' then table.insert(fences, f) end
end
if ARGV[2] == '1' then table.insert(fences, '__fence:' .. ARGV[1]) end
redis.call('DEL', key)
if #fences > 0 then
  for _, f in ipairs(fences) do redis.call('HSET', key, f, '1') end
  redis.call('EXPIRE', key, ARGV[3])
end
return #fences
";

    private async Task InvalidateAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear, string claimId, bool fence)
    {
        var db = _redis.GetDatabase();
        var args = new RedisValue[] { claimId, fence ? "1" : "0", (long)DefaultKeyTtl.TotalSeconds };
        await Task.WhenAll(
            db.ScriptEvaluateAsync(InvalidateScript,
                new RedisKey[] { MakeKey(memberId, AccumulatorScope.Individual, benefitPlanId, planYear) }, args),
            db.ScriptEvaluateAsync(InvalidateScript,
                new RedisKey[] { MakeKey(subscriberId, AccumulatorScope.Family, benefitPlanId, planYear) }, args));

        _logger.LogInformation(
            "Invalidated accumulator cache for claim reversal {ClaimId} (member {MemberId}, fenced: {Fenced})",
            SanitizeForLog(claimId), SanitizeForLog(memberId), fence);
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

        // Try Redis first. Marker fields (commit records, fences) are not
        // amounts: a hash holding only those is a miss.
        var entries = (await db.HashGetAllAsync(key)).Where(e => !IsMarkerField(e.Name)).ToArray();

        if (entries.Length > 0)
        {
            return ParseSnapshots(entries, scope);
        }

        // Cache miss — rebuild from claim history
        _logger.LogDebug(
            "Cache miss for {Key}. Rebuilding from claim history.", key.ToString());

        var rebuild = await _claimsSource.CalculateAccumulatorsWithClaimsAsync(
            _tenantContext.TenantId, ownerId, scope, benefitPlanId, planYear, ct);

        if (!rebuild.Success)
        {
            // Source was unavailable — do not cache; the next read will retry.
            _logger.LogWarning(
                "Accumulator source unavailable for {Key}. Skipping Redis cache population.",
                key.ToString());
            return [];
        }

        // Populate Redis atomically with one journal record per counted claim,
        // so a later commit can undo exactly what the rebuild put in for it
        // (a replacement taking its predecessor back, a re-commit of the same
        // claim) and pricing can exclude a claim's own contribution.
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var snapshot in rebuild.Snapshots)
        {
            var field = MakeField(snapshot.Type, snapshot.NetworkTier).ToString();
            totals[field] = (totals.TryGetValue(field, out var t) ? t : 0d) + (double)snapshot.AccumulatedAmountAfter;
        }
        var claims = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var claim in rebuild.Claims)
        {
            var journal = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var u in claim.Updates.Where(u => u.Amount > 0))
            {
                var field = MakeField(u).ToString();
                var amount = (double)u.Amount + (journal.TryGetValue(field, out var e) ? ((JournalEntry)e).a : 0d);
                journal[field] = new JournalEntry(amount, u.Type.ToString(), u.NetworkTier.ToString(), u.Source ?? string.Empty);
            }
            if (journal.Count > 0) claims[claim.ClaimId] = journal;
        }

        var outcome = await db.ScriptEvaluateAsync(RebuildScript, new[] { key }, new RedisValue[]
        {
            (long)DefaultKeyTtl.TotalSeconds,
            System.Text.Json.JsonSerializer.Serialize(totals),
            claims.Count == 0 ? "{}" : System.Text.Json.JsonSerializer.Serialize(claims),
        });

        if (outcome.ToString() == "SKIPPED")
        {
            // Another writer populated it meanwhile: read what is there.
            var now = (await db.HashGetAllAsync(key)).Where(e => !IsMarkerField(e.Name)).ToArray();
            return ParseSnapshots(now, scope);
        }

        _logger.LogDebug(
            "Rebuilt {Count} accumulators ({Claims} claim record(s)) for {Key} from claim history.",
            rebuild.Snapshots.Count, claims.Count, key.ToString());

        return rebuild.Snapshots.ToList();
    }

    /// <summary>One journalled field (Lua reads <c>a</c>, <c>t</c>, <c>n</c>, <c>s</c>).</summary>
    private sealed record JournalEntry(double a, string t, string n, string s);

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
