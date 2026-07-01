using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using Microsoft.Azure.Cosmos;
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

    /// <summary>Upsert a single rule document.</summary>
    Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default);

    /// <summary>Bulk upsert — used by the seed data loader.</summary>
    Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default);

    /// <summary>Delete a rule by ID and state code (partition key).</summary>
    Task DeleteAsync(string ruleId, string stateCode, CancellationToken ct = default);

    /// <summary>List all rules for a tenant — used by the portal admin grid.</summary>
    Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// Cosmos DB implementation
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Cosmos DB implementation of IPaRuleRepository.
///
/// Container: prior-auth-rules
/// Partition key: /stateCode
///   Rules are almost always queried by state — this keeps queries
///   within a single logical partition at decision time.
///
/// Document ID: "{stateCode}:{lob}:{program ?? "any"}:{tenantId ?? "platform"}:{ruleId}"
/// </summary>
public sealed class PaRuleRepositoryCosmos : IPaRuleRepository
{
    private readonly Container _container;
    private readonly ILogger<PaRuleRepositoryCosmos> _logger;

    public PaRuleRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<PaRuleRepositoryCosmos> logger)
    {
        var db        = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var container = configuration["PriorAuthRuleEngine:RulesContainer"] ?? "prior-auth-rules";
        _container    = cosmosClient.GetContainer(db, container);
        _logger       = logger;
    }

    public async Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
    {
        var tenantFilter = key.TenantId is null
            ? "IS_NULL(c.tenantId)"
            : "c.tenantId = @tenantId";

        var programFilter = key.Program is null
            ? "IS_NULL(c.program)"
            : "c.program = @program";

        var query = new QueryDefinition(
            $"SELECT * FROM c " +
            $"WHERE c.stateCode = @stateCode " +
            $"AND c.lob = @lob " +
            $"AND {programFilter} " +
            $"AND {tenantFilter} " +
            $"AND c.isEnabled = true " +
            $"ORDER BY c.category, c.priority")
            .WithParameter("@stateCode", key.StateCode)
            .WithParameter("@lob", (int)key.Lob);

        if (key.Program is not null) query = query.WithParameter("@program", key.Program);
        if (key.TenantId is not null) query = query.WithParameter("@tenantId", key.TenantId);

        var iterator = _container.GetItemQueryIterator<PaRuleDocument>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(key.StateCode)
            });

        var results = new List<PaRuleDocument>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }

        return results;
    }

    public async Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default)
    {
        rule.Id        = MakeId(rule);
        rule.UpdatedAt = DateTime.UtcNow;
        await _container.UpsertItemAsync(rule, new PartitionKey(rule.StateCode), cancellationToken: ct);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
    {
        var tasks = rules.Select(r =>
        {
            r.Id = MakeId(r); r.UpdatedAt = DateTime.UtcNow;
            return _container.UpsertItemAsync(r, new PartitionKey(r.StateCode), cancellationToken: ct);
        });
        await Task.WhenAll(tasks);
    }

    public async Task DeleteAsync(
        string ruleId, string stateCode, CancellationToken ct = default)
    {
        // Id requires full composite — query first
        var query = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.ruleId = @ruleId AND c.stateCode = @stateCode")
            .WithParameter("@ruleId",    ruleId)
            .WithParameter("@stateCode", stateCode);

        var iterator = _container.GetItemQueryIterator<IdOnly>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(stateCode) });

        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(ct))
            {
                try
                {
                    await _container.DeleteItemAsync<PaRuleDocument>(
                        item.Id, new PartitionKey(stateCode), cancellationToken: ct);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
        }
    }

    public async Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
    {
        var sql    = "SELECT * FROM c WHERE 1=1";
        var qd     = new QueryDefinition(sql);
        if (tenantId  is not null) { sql += " AND c.tenantId = @tenantId";   qd = new QueryDefinition(sql).WithParameter("@tenantId",  tenantId); }
        if (stateCode is not null) { sql += " AND c.stateCode = @stateCode"; qd = new QueryDefinition(sql).WithParameter("@stateCode", stateCode); }

        var opts = stateCode is not null
            ? new QueryRequestOptions { PartitionKey = new PartitionKey(stateCode) }
            : new QueryRequestOptions();

        var iterator = _container.GetItemQueryIterator<PaRuleDocument>(
            new QueryDefinition(sql), requestOptions: opts);

        var results = new List<PaRuleDocument>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    private static string MakeId(PaRuleDocument r) =>
        $"{r.StateCode}:{(int)r.Lob}:{r.Program ?? "any"}:{r.TenantId ?? "platform"}:{r.RuleId}";

    private sealed record IdOnly { public string Id { get; init; } = string.Empty; }
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
        var filter = Builders<PaRuleDocument>.Filter.And(
            Builders<PaRuleDocument>.Filter.Eq(r => r.RuleId,    rule.RuleId),
            Builders<PaRuleDocument>.Filter.Eq(r => r.StateCode, rule.StateCode));

        await _collection.ReplaceOneAsync(filter, rule,
            new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
    {
        var ops = rules.Select(r =>
        {
            r.Id        = MakeId(r);
            r.UpdatedAt = DateTime.UtcNow;
            var filter = Builders<PaRuleDocument>.Filter.And(
                Builders<PaRuleDocument>.Filter.Eq(x => x.RuleId,    r.RuleId),
                Builders<PaRuleDocument>.Filter.Eq(x => x.StateCode, r.StateCode));
            return new ReplaceOneModel<PaRuleDocument>(filter, r) { IsUpsert = true };
        }).ToList();

        if (ops.Count > 0)
            await _collection.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    private static string MakeId(PaRuleDocument r) =>
        $"{r.StateCode}:{(int)r.Lob}:{r.Program ?? "any"}:{r.TenantId ?? "platform"}:{r.RuleId}";

    public async Task DeleteAsync(string ruleId, string stateCode, CancellationToken ct = default)
    {
        var filter = Builders<PaRuleDocument>.Filter.And(
            Builders<PaRuleDocument>.Filter.Eq(r => r.RuleId,    ruleId),
            Builders<PaRuleDocument>.Filter.Eq(r => r.StateCode, stateCode));
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

    public async Task DeleteAsync(string ruleId, string stateCode, CancellationToken ct = default)
    {
        await _inner.DeleteAsync(ruleId, stateCode, ct);
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
