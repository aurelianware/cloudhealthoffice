using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests.Persistence;

public class RedisPaRuleRepositoryTests
{
    private readonly IPaRuleRepository _inner;
    private readonly IDatabase _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisPaRuleRepository _sut;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RuleSetKey TxStarKey = new()
    {
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Program = "STAR",
        TenantId = "pchp"
    };

    public RedisPaRuleRepositoryTests()
    {
        _inner = Substitute.For<IPaRuleRepository>();
        _db = Substitute.For<IDatabase>();
        _redis = Substitute.For<IConnectionMultiplexer>();

        // GetDatabase() is called in the constructor — must return _db
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);
        // Needed by TryFlushStateAsync → _db.Multiplexer
        _db.Multiplexer.Returns(_redis);

        _sut = new RedisPaRuleRepository(
            _inner,
            _redis,
            Options.Create(new PriorAuthRuleEngineOptions { RuleSetCacheTtl = Ttl }),
            Substitute.For<ILogger<RedisPaRuleRepository>>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PaRuleDocument MakeRule(
        string ruleId = "TX-STAR-REG-001",
        string stateCode = "TX",
        PaLineOfBusiness lob = PaLineOfBusiness.Medicaid,
        string? program = "STAR",
        string? tenantId = "pchp") => new()
    {
        RuleId = ruleId,
        RuleName = $"Rule {ruleId}",
        StateCode = stateCode,
        Lob = lob,
        Program = program,
        TenantId = tenantId,
        Category = RuleCategory.RegulatoryExemption,
        Scope = tenantId is null ? RuleScope.Platform : RuleScope.Tenant,
        Priority = 1,
        RuleType = "TxGoldCardExemption"
    };

    // ── 1. Cache hit ─────────────────────────────────────────────────

    [Fact]
    public async Task GetRulesAsync_CacheHit_ReturnsDeserializedRules_WithoutHittingInner()
    {
        var rules = new List<PaRuleDocument> { MakeRule() };
        var json = JsonSerializer.Serialize(rules, JsonOpts);

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)json);

        var result = await _sut.GetRulesAsync(TxStarKey);

        result.Should().HaveCount(1);
        result[0].RuleId.Should().Be("TX-STAR-REG-001");

        await _inner.DidNotReceive()
            .GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>());
    }

    // ── 2. Cache miss ────────────────────────────────────────────────

    [Fact]
    public async Task GetRulesAsync_CacheMiss_CallsInner_CachesResult_WithCorrectTtl()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        var rules = new List<PaRuleDocument> { MakeRule() };
        _inner.GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>())
            .Returns(rules);

        var result = await _sut.GetRulesAsync(TxStarKey);

        result.Should().HaveCount(1);

        await _inner.Received(1)
            .GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>());

        await _db.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Ttl,
            Arg.Any<bool>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    // ── 3. Redis throws ──────────────────────────────────────────────

    [Fact]
    public async Task GetRulesAsync_RedisThrows_FallsThroughToInner()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var rules = new List<PaRuleDocument> { MakeRule() };
        _inner.GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>())
            .Returns(rules);

        var result = await _sut.GetRulesAsync(TxStarKey);

        result.Should().HaveCount(1);
        await _inner.Received(1)
            .GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>());
    }

    // ── 4. Cache key format ──────────────────────────────────────────

    [Fact]
    public async Task GetRulesAsync_CacheKey_Format_IsCorrect()
    {
        // PaLineOfBusiness.Medicaid = 3
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);
        _inner.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PaRuleDocument>());

        await _sut.GetRulesAsync(TxStarKey);

        await _db.Received().StringGetAsync(
            (RedisKey)"pa-rules:TX:3:STAR:pchp",
            Arg.Any<CommandFlags>());
    }

    // ── 5. Upsert invalidates ────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_WritesToInner_ThenInvalidatesCacheKey()
    {
        var rule = MakeRule();

        await _sut.UpsertAsync(rule);

        await _inner.Received(1).UpsertAsync(rule, Arg.Any<CancellationToken>());

        await _db.Received(1).KeyDeleteAsync(
            (RedisKey)"pa-rules:TX:3:STAR:pchp",
            Arg.Any<CommandFlags>());
    }

    // ── 6. BulkUpsert deduplicates keys ──────────────────────────────

    [Fact]
    public async Task BulkUpsertAsync_InvalidatesAllAffectedKeys_Deduplicated()
    {
        // 3 rules: 2 share the same key (TX/Medicaid/STAR/pchp), 1 different (TX/Medicaid/STARPlus/pchp)
        var rules = new[]
        {
            MakeRule(ruleId: "R1", program: "STAR"),
            MakeRule(ruleId: "R2", program: "STAR"),
            MakeRule(ruleId: "R3", program: "STARPlus")
        };

        await _sut.BulkUpsertAsync(rules);

        await _inner.Received(1)
            .BulkUpsertAsync(Arg.Any<IEnumerable<PaRuleDocument>>(), Arg.Any<CancellationToken>());

        // KeyDeleteAsync(RedisKey[]) — should be called with exactly 2 distinct keys
        await _db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(keys => keys.Length == 2),
            Arg.Any<CommandFlags>());
    }

    // ── 7. Delete flushes state keys via SCAN ────────────────────────

    [Fact]
    public async Task DeleteAsync_FlushesAllKeysForState_UsingScan()
    {
        var server = Substitute.For<IServer>();
        var endpoint = new DnsEndPoint("localhost", 6379);

        _redis.GetEndPoints(Arg.Any<bool>()).Returns(new EndPoint[] { endpoint });
        _redis.GetServer(endpoint, Arg.Any<object>()).Returns(server);

        // Server.Keys returns matching keys
        server.Keys(
            Arg.Any<int>(),
            Arg.Is<RedisValue>(v => v.ToString().Contains("pa-rules:TX:*")),
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<CommandFlags>())
            .Returns(new RedisKey[] { "pa-rules:TX:3:STAR:pchp", "pa-rules:TX:3:any:platform" });

        await _sut.DeleteAsync("TX-STAR-REG-001", "TX");

        await _inner.Received(1)
            .DeleteAsync("TX-STAR-REG-001", "TX", Arg.Any<CancellationToken>());

        // KeyDeleteAsync called with the 2 keys from SCAN
        await _db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(keys => keys.Length == 2),
            Arg.Any<CommandFlags>());
    }
}
