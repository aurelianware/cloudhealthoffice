using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Text.Json;

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
            r.UpdatedAt = DateTime.UtcNow;
            var filter = Builders<PaRuleDocument>.Filter.And(
                Builders<PaRuleDocument>.Filter.Eq(x => x.RuleId,    r.RuleId),
                Builders<PaRuleDocument>.Filter.Eq(x => x.StateCode, r.StateCode));
            return new ReplaceOneModel<PaRuleDocument>(filter, r) { IsUpsert = true };
        }).ToList();

        if (ops.Count > 0)
            await _collection.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
    }

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
// Redis caching decorator
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Redis read-through cache for PA rule sets.
///
/// Cache key:  pa-rules:{stateCode}:{lob}:{program ?? "any"}:{tenantId ?? "platform"}
/// TTL:        15 minutes (PriorAuthRuleEngineOptions.RuleSetCacheTtl)
/// Invalidation: UpsertAsync and DeleteAsync delete affected cache keys.
///
/// Rule sets change only at onboarding or admin update — 15-minute TTL
/// provides a good balance between freshness and Redis pressure.
/// </summary>
public sealed class RedisPaRuleRepository : IPaRuleRepository
{
    private readonly IPaRuleRepository _inner;
    private readonly IDatabase _db;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisPaRuleRepository> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RedisPaRuleRepository(
        IPaRuleRepository inner,
        IConnectionMultiplexer redis,
        IOptions<PriorAuthRuleEngineOptions> options,
        ILogger<RedisPaRuleRepository> logger)
    {
        _inner  = inner;
        _db     = redis.GetDatabase();
        _ttl    = options.Value.RuleSetCacheTtl;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
    {
        var cacheKey = MakeCacheKey(key);

        try
        {
            var cached = await _db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                var rules = JsonSerializer.Deserialize<List<PaRuleDocument>>(cached!, _json);
                if (rules is not null)
                {
                    _logger.LogDebug("PA rule cache hit: {Key}", cacheKey);
                    return rules;
                }
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for PA rule read — falling through");
        }

        var fresh = await _inner.GetRulesAsync(key, ct);

        try
        {
            var payload = JsonSerializer.Serialize(fresh, _json);
            await _db.StringSetAsync(cacheKey, payload, _ttl);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to cache PA rules for key {Key}", cacheKey);
        }

        return fresh;
    }

    public async Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default)
    {
        await _inner.UpsertAsync(rule, ct);
        await TryInvalidateAsync(rule);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
    {
        var list = rules.ToList();
        await _inner.BulkUpsertAsync(list, ct);

        // Invalidate all unique cache keys touched by the bulk write
        var keys = list
            .Select(r => MakeCacheKey(new RuleSetKey
            {
                StateCode = r.StateCode,
                Lob       = r.Lob,
                Program   = r.Program,
                TenantId  = r.TenantId
            }))
            .Distinct()
            .ToArray();

        try { await _db.KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray()); }
        catch (RedisException ex) { _logger.LogWarning(ex, "Failed to invalidate PA rule cache"); }
    }

    public async Task DeleteAsync(string ruleId, string stateCode, CancellationToken ct = default)
    {
        await _inner.DeleteAsync(ruleId, stateCode, ct);
        // Cannot derive a precise cache key without knowing lob/program/tenantId,
        // so flush all keys for this state — conservative but safe.
        await TryFlushStateAsync(stateCode);
    }

    public Task<IReadOnlyList<PaRuleDocument>> ListAsync(
        string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
        => _inner.ListAsync(tenantId, stateCode, ct); // admin path — not cached

    private static string MakeCacheKey(RuleSetKey key) =>
        $"pa-rules:{key.StateCode}:{(int)key.Lob}:{key.Program ?? "any"}:{key.TenantId ?? "platform"}";

    private async Task TryInvalidateAsync(PaRuleDocument rule)
    {
        var key = MakeCacheKey(new RuleSetKey
        {
            StateCode = rule.StateCode,
            Lob       = rule.Lob,
            Program   = rule.Program,
            TenantId  = rule.TenantId
        });
        try { await _db.KeyDeleteAsync(key); }
        catch (RedisException ex) { _logger.LogWarning(ex, "Failed to invalidate {Key}", key); }
    }

    private async Task TryFlushStateAsync(string stateCode)
    {
        // Scan for all pa-rules:{stateCode}:* keys and delete them
        // Use SCAN — never KEYS in production
        var server  = _db.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
        var pattern = $"pa-rules:{stateCode}:*";
        try
        {
            var keys = server.Keys(pattern: pattern).Select(k => (RedisKey)k.ToString()).ToArray();
            if (keys.Length > 0) await _db.KeyDeleteAsync(keys);
        }
        catch (RedisException ex) { _logger.LogWarning(ex, "Failed to flush state {State} from cache", stateCode); }
    }
}
