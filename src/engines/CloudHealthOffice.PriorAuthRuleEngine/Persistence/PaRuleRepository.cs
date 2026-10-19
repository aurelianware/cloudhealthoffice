using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StackExchange.Redis;

namespace CloudHealthOffice.PriorAuthRuleEngine.Persistence;

// ─────────────────────────────────────────────────────────────────
// Interface
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Storage interface for PA rule documents.
///
/// Rules are stored per (StateCode, Lob, Program, TenantId) and retrieved
/// as an ordered set. Platform rules (TenantId = null) are merged with
/// tenant-specific overrides by the engine's resolver.
/// </summary>
public interface IPaRuleRepository
{
    /// <summary>
    /// Return all enabled rules matching the given RuleSetKey.
    /// Exact match on StateCode + Lob + Program + TenantId.
    /// The engine's resolver handles the fallback hierarchy externally.
    /// </summary>
    Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
        RuleSetKey key, CancellationToken ct = default);

    /// <summary>
    /// Upsert a single rule document. Writes only the document identified by
    /// the rule's full identity (state, LOB, program, tenant, rule id): a
    /// tenant's write never reaches another tenant's or the platform's copy of
    /// the same RuleId (D-16).
    /// </summary>
    Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default);

    /// <summary>Bulk upsert — used by the seed data loader. Same scoping as <see cref="UpsertAsync"/>.</summary>
    Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default);

    /// <summary>
    /// Delete a rule by ID and state code within ONE scope:
    /// <paramref name="tenantId"/> names the tenant whose rule is deleted, and
    /// null means the platform rule. Other tenants' (and, for a tenant delete,
    /// the platform's) rules with the same RuleId are untouched (D-16).
    /// </summary>
    Task DeleteAsync(string ruleId, string stateCode, string? tenantId, CancellationToken ct = default);

    /// <summary>List all rules for a tenant — used by the portal admin grid.</summary>
    Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// MongoDB implementation
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// MongoDB implementation of IPaRuleRepository.
///
/// Collection: prior_auth_rules
/// Indexes:
///   { stateCode:1, lob:1, program:1, tenantId:1, isEnabled:1 } — primary query
///   { ruleId:1, stateCode:1 }                                  — delete by ruleId
/// </summary>
public sealed class PaRuleRepositoryMongo : IPaRuleRepository
{
    private readonly IMongoCollection<PaRuleDocument> _collection;
    private readonly ILogger<PaRuleRepositoryMongo> _logger;

    public PaRuleRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<PaRuleRepositoryMongo> logger)
    {
        var collectionName = configuration["PriorAuthRuleEngine:RulesCollection"] ?? "prior_auth_rules";
        _collection = database.GetCollection<PaRuleDocument>(collectionName);
        _logger     = logger;
        EnsureIndexes();
    }

    public async Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
    {
        var fb = Builders<PaRuleDocument>.Filter;
        var filter = fb.And(
            fb.Eq(r => r.StateCode,  key.StateCode),
            fb.Eq(r => r.Lob,        key.Lob),
            key.Program  is null ? fb.Eq(r => r.Program,  null!) : fb.Eq(r => r.Program,  key.Program),
            key.TenantId is null ? fb.Eq(r => r.TenantId, null!) : fb.Eq(r => r.TenantId, key.TenantId),
            fb.Eq(r => r.IsEnabled, true));

        var sort = Builders<PaRuleDocument>.Sort
            .Ascending(r => r.Category)
            .Ascending(r => r.Priority);

        return await _collection.Find(filter).Sort(sort).ToListAsync(ct);
    }

    public async Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default)
    {
        rule.Id        = MakeId(rule);
        rule.UpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(OwnDocument(rule), rule,
            new ReplaceOptions { IsUpsert = true }, ct);
    }

    /// <summary>
    /// The one document a write of <paramref name="rule"/> may replace: its
    /// own composite id, which embeds state, LOB, program, tenant (or
    /// "platform") and rule id, with the tenant also matched explicitly.
    ///
    /// The filter used to be RuleId + StateCode only (D-16), which matched
    /// another tenant's — or the platform's — document with the same RuleId.
    /// Mongo's immutable <c>_id</c> turned that into a write error rather than
    /// a silent overwrite, so a tenant could not override a platform RuleId at
    /// all, and the write path depended on what other tenants had stored.
    /// </summary>
    private static FilterDefinition<PaRuleDocument> OwnDocument(PaRuleDocument rule)
    {
        var fb = Builders<PaRuleDocument>.Filter;
        return fb.And(
            fb.Eq(r => r.Id, rule.Id),
            fb.Eq(r => r.TenantId, rule.TenantId));
    }

    public async Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
    {
        var ops = rules.Select(r =>
        {
            r.Id        = MakeId(r);
            r.UpdatedAt = DateTime.UtcNow;
            return new ReplaceOneModel<PaRuleDocument>(OwnDocument(r), r) { IsUpsert = true };
        }).ToList();

        if (ops.Count > 0)
            await _collection.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    private static string MakeId(PaRuleDocument r) =>
        $"{r.StateCode}:{(int)r.Lob}:{r.Program ?? "any"}:{r.TenantId ?? "platform"}:{r.RuleId}";

    public async Task DeleteAsync(
        string ruleId, string stateCode, string? tenantId, CancellationToken ct = default)
    {
        // Scoped to one tenant, or to the platform (tenantId null). It used to
        // delete every tenant's copy of the RuleId in the state (D-16). Many,
        // not one: the same RuleId may exist per LOB/program within the scope.
        var fb = Builders<PaRuleDocument>.Filter;
        var filter = fb.And(
            fb.Eq(r => r.RuleId,    ruleId),
            fb.Eq(r => r.StateCode, stateCode),
            fb.Eq(r => r.TenantId,  tenantId));
        await _collection.DeleteManyAsync(filter, ct);
    }

    public async Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<PaRuleDocument>>();
        if (tenantId  is not null) filters.Add(Builders<PaRuleDocument>.Filter.Eq(r => r.TenantId,  tenantId));
        if (stateCode is not null) filters.Add(Builders<PaRuleDocument>.Filter.Eq(r => r.StateCode, stateCode));

        var filter = filters.Count > 0
            ? Builders<PaRuleDocument>.Filter.And(filters)
            : Builders<PaRuleDocument>.Filter.Empty;

        return await _collection.Find(filter).ToListAsync(ct);
    }

    private void EnsureIndexes()
    {
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<PaRuleDocument>(
                Builders<PaRuleDocument>.IndexKeys
                    .Ascending(r => r.StateCode)
                    .Ascending(r => r.Lob)
                    .Ascending(r => r.Program)
                    .Ascending(r => r.TenantId)
                    .Ascending(r => r.IsEnabled),
                new CreateIndexOptions { Name = "idx_ruleset_lookup" }),

            new CreateIndexModel<PaRuleDocument>(
                Builders<PaRuleDocument>.IndexKeys
                    .Ascending(r => r.RuleId)
                    .Ascending(r => r.StateCode),
                new CreateIndexOptions { Name = "idx_ruleId_state" })
        });
    }
}

// ─────────────────────────────────────────────────────────────────
// Cache-wrapped rule repository
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Read-through cache for PA rule sets.
///
/// Cache key:   pa-rules:{stateCode}:{lob}:{program ?? "any"}:{tenantId ?? "platform"}
/// TTL:         15 minutes (PriorAuthRuleEngineOptions.RuleSetCacheTtl)
/// Invalidation: UpsertAsync + BulkUpsertAsync invalidate exact keys;
///               DeleteAsync flushes every key under pa-rules:{state}:* via SCAN.
///
/// ── Why this class still depends on IConnectionMultiplexer ────────
///
/// This repository is one of two deliberate exceptions to the shared
/// <see cref="ICacheProvider"/> abstraction (the other is
/// <c>RedisAccumulatorService</c> in BenefitEngine). The K/V
/// operations (read, set, single-key delete, bulk delete) flow through
/// <see cref="ICacheProvider"/> and benefit from its guard layer,
/// serialization, and coalescing. The exception is the
/// <see cref="DeleteAsync"/> invalidation path: a single rule delete
/// cannot reconstruct the cache key set because the cache is keyed on
/// <c>(stateCode, lob, program, tenantId)</c> and the caller only
/// supplies <c>(ruleId, stateCode)</c>. The conservative-but-correct
/// fix is a Redis <c>SCAN</c> for <c>pa-rules:{state}:*</c> — an
/// operation that <see cref="ICacheProvider"/> deliberately does NOT
/// expose (pattern deletion is expensive on large key spaces and
/// leaks Redis semantics into a neutral abstraction).
///
/// We therefore accept a second, bounded dependency on
/// <see cref="IConnectionMultiplexer"/> rather than either (a) bending
/// the shared interface to accommodate a pattern-delete that nobody
/// else needs, or (b) changing the rule-delete semantics to force
/// callers into state-level invalidation. See
/// <c>docs/architecture/shared-cache.md</c> and Addendum A.7.2.
/// </summary>
public sealed class RedisPaRuleRepository : IPaRuleRepository
{
    private readonly IPaRuleRepository _inner;
    private readonly ICacheProvider _cache;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly CacheKeyGuard _keyGuard;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisPaRuleRepository> _logger;

    /// <summary>
    /// <paramref name="multiplexer"/> is optional: it is null when the
    /// cache backend resolves to InMemory or Null, in which case the
    /// SCAN-based state flush degrades to a debug log + no-op. Exact-key
    /// invalidation on Upsert/BulkUpsert continues to work via
    /// <see cref="ICacheProvider"/>. This lets fhir-service and any other
    /// host wire <c>WithRuleCache()</c> unconditionally without the
    /// previous "Redis must be present" gate.
    /// </summary>
    public RedisPaRuleRepository(
        IPaRuleRepository inner,
        ICacheProvider cache,
        IConnectionMultiplexer? multiplexer,
        CacheKeyGuard keyGuard,
        IOptions<PriorAuthRuleEngineOptions> options,
        ILogger<RedisPaRuleRepository> logger)
    {
        _inner       = inner;
        _cache       = cache;
        _multiplexer = multiplexer;
        _keyGuard    = keyGuard;
        _ttl         = options.Value.RuleSetCacheTtl;
        _logger      = logger;
    }

    public async Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
    {
        // Rule sets are serialized as CachedRuleSet (a list wrapper) because
        // ICacheProvider.GetOrSetAsync<T> requires T : class. The wrapper is
        // stable over time and adds a trivial allocation per read.
        var result = await _cache.GetOrSetAsync<CachedRuleSet>(
            MakeCacheKey(key),
            async token =>
            {
                var rules = await _inner.GetRulesAsync(key, token);
                return new CachedRuleSet(rules.ToList());
            },
            _ttl,
            // Rule sets straddle tenant/platform data. Keying includes the
            // tenant (or "platform" sentinel), but the tenant scope on the
            // guard requires an HttpContext. Platform rules are legitimately
            // global — so we use Global here and rely on the embedded
            // tenantId in the logical key for multi-tenant uniqueness.
            CacheScope.Global,
            ct);

        return (IReadOnlyList<PaRuleDocument>?)result?.Rules ?? Array.Empty<PaRuleDocument>();
    }

    public async Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default)
    {
        await _inner.UpsertAsync(rule, ct);
        await _cache.RemoveAsync(MakeCacheKey(new RuleSetKey
        {
            StateCode = rule.StateCode,
            Lob       = rule.Lob,
            Program   = rule.Program,
            TenantId  = rule.TenantId
        }), CacheScope.Global, ct);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
    {
        var list = rules.ToList();
        await _inner.BulkUpsertAsync(list, ct);

        var keys = list
            .Select(r => MakeCacheKey(new RuleSetKey
            {
                StateCode = r.StateCode,
                Lob       = r.Lob,
                Program   = r.Program,
                TenantId  = r.TenantId
            }))
            .Distinct()
            .ToList();

        if (keys.Count > 0)
            await _cache.RemoveAsync(keys, CacheScope.Global, ct);
    }

    public async Task DeleteAsync(
        string ruleId, string stateCode, string? tenantId, CancellationToken ct = default)
    {
        await _inner.DeleteAsync(ruleId, stateCode, tenantId, ct);
        // The cache key cannot be reconstructed from (ruleId, stateCode) alone,
        // so flush every pa-rules:{state}:* key via SCAN. Uses the direct
        // multiplexer because pattern deletion is not exposed on
        // ICacheProvider by design — see class remarks above and
        // docs/architecture/shared-cache.md.
        await FlushStateViaScanAsync(stateCode);
    }

    public Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
        => _inner.ListAsync(tenantId, stateCode, ct); // admin path — not cached

    private static string MakeCacheKey(RuleSetKey key) =>
        $"pa-rules:{key.StateCode}:{(int)key.Lob}:{key.Program ?? "any"}:{key.TenantId ?? "platform"}";

    private async Task FlushStateViaScanAsync(string stateCode)
    {
        if (_multiplexer is null)
        {
            // Cache backend is InMemory or Null; SCAN is unreachable and
            // also unnecessary. InMemory entries are process-local — they
            // will expire via TTL, and exact-key invalidation on Upsert /
            // BulkUpsert already handles the common write path. Skip.
            _logger.LogDebug(
                "PA rule state-flush skipped for {State}: no IConnectionMultiplexer " +
                "(cache backend is not Redis). Entries will expire via TTL.",
                stateCode);
            return;
        }

        // TODO(scale): SCAN across every pa-rules:{state}:* key is fine at
        // today's rule cardinality (~hundreds of keys per state) but becomes
        // expensive once a state carries thousands of (lob, program, tenant)
        // combinations. If this shows up in Redis SLOWLOG, switch to an
        // explicit per-state index set (SADD pa-rules:{state}:index on write;
        // SMEMBERS + DEL on flush). Tracked as a follow-up.
        //
        // The pattern is ANCHORED at CacheKeyGuard's deterministic prefix
        // ({env}:_global:) so SCAN traverses only keys in this env + scope
        // instead of the full keyspace. Rule-set writes go through
        // CacheScope.Global (see GetRulesAsync / UpsertAsync), so Global is
        // the correct scope to flush here.
        try
        {
            var prefix  = _keyGuard.BuildPrefix(CacheScope.Global);
            var pattern = $"{prefix}pa-rules:{stateCode}:*";
            var server  = _multiplexer.GetServer(_multiplexer.GetEndPoints().First());
            var keys    = server.Keys(pattern: pattern).ToArray();
            if (keys.Length > 0)
                await _multiplexer.GetDatabase().KeyDeleteAsync(keys);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to flush state {State} from PA rule cache", stateCode);
        }
    }

}

/// <summary>
/// Serialization wrapper — <see cref="ICacheProvider.GetOrSetAsync{T}"/>
/// requires T : class, and <see cref="IReadOnlyList{T}"/> is an interface
/// type that the JSON serializer can't reliably materialize on the read
/// path without an explicit concrete type to bind to. Wrapping the list in
/// a concrete record gives the serializer a fixed shape to round-trip.
/// Internal so the tests-assembly can reference the exact generic type
/// instantiation of <see cref="ICacheProvider.GetOrSetAsync{T}"/> when
/// setting up NSubstitute expectations — generic method match is
/// per-T, so a mock set up for <c>GetOrSetAsync&lt;object&gt;</c> does
/// NOT intercept <c>GetOrSetAsync&lt;CachedRuleSet&gt;</c>.
/// </summary>
internal sealed record CachedRuleSet(List<PaRuleDocument> Rules);
