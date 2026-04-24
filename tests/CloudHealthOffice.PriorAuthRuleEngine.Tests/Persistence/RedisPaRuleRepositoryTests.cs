using System.Net;
using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests.Persistence;

/// <summary>
/// Post-A.7.2 tests: the K/V operations go through <see cref="ICacheProvider"/>
/// and we mock it directly. The SCAN-based state flush still uses
/// <see cref="IConnectionMultiplexer"/> — the deliberate exception — so the
/// delete path tests still mock <c>IServer</c> for Keys() and <c>IDatabase</c>
/// for KeyDelete().
/// </summary>
public class RedisPaRuleRepositoryTests
{
    private readonly IPaRuleRepository _inner = Substitute.For<IPaRuleRepository>();
    private readonly ICacheProvider _cache = Substitute.For<ICacheProvider>();
    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
    private readonly RedisPaRuleRepository _sut;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly RuleSetKey TxStarKey = new()
    {
        StateCode = "TX",
        Lob       = PaLineOfBusiness.Medicaid,
        Program   = "STAR",
        TenantId  = "txmco01"
    };
    private const string TxStarCacheKey = "pa-rules:TX:3:STAR:txmco01";

    private readonly CacheKeyGuard _keyGuard = BuildGuard();

    public RedisPaRuleRepositoryTests()
    {
        _sut = new RedisPaRuleRepository(
            _inner,
            _cache,
            _multiplexer,
            _keyGuard,
            Options.Create(new PriorAuthRuleEngineOptions { RuleSetCacheTtl = Ttl }),
            Substitute.For<ILogger<RedisPaRuleRepository>>());
    }

    private static CacheKeyGuard BuildGuard()
    {
        var accessor = new HttpContextAccessor();
        var env = new FakeEnv { EnvironmentName = "test" };
        return new CacheKeyGuard(accessor, env);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "test";
        public string ApplicationName { get; set; } = "cho-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task GetRulesAsync_DelegatesToCacheProvider_WithGlobalScope()
    {
        // Match on the exact generic instantiation the SUT invokes —
        // NSubstitute matches generic methods per-T, so a setup for
        // GetOrSetAsync<object> does NOT intercept GetOrSetAsync<CachedRuleSet>.
        _cache.GetOrSetAsync<CachedRuleSet>(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CachedRuleSet?>>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CacheScope>(),
            Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<CachedRuleSet?>(null));

        await _sut.GetRulesAsync(TxStarKey);

        // Verify exactly one GetOrSetAsync with the expected key + scope.
        var calls = _cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICacheProvider.GetOrSetAsync))
            .ToList();
        calls.Should().HaveCount(1);

        var args = calls[0].GetArguments();
        args[0].Should().Be(TxStarCacheKey);
        args[2].Should().Be(Ttl);
        args[3].Should().Be(CacheScope.Global);
    }

    [Fact]
    public async Task GetRulesAsync_FactoryDelegatesToInner_AndReturnsWrappedRules()
    {
        var rules = new List<PaRuleDocument> { MakeRule() };
        _inner.GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>()).Returns(rules);

        // Invoke the factory the SUT passes in — verify it hits the inner
        // repo and that the SUT unwraps the CachedRuleSet shape correctly.
        _cache.GetOrSetAsync<CachedRuleSet>(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<CachedRuleSet?>>>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CacheScope>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(async ci =>
            {
                var factory = ci.ArgAt<Func<CancellationToken, Task<CachedRuleSet?>>>(1);
                return await factory(CancellationToken.None);
            });

        var result = await _sut.GetRulesAsync(TxStarKey);

        result.Should().HaveCount(1);
        result[0].RuleId.Should().Be("TX-STAR-REG-001");
        await _inner.Received(1).GetRulesAsync(TxStarKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_WritesToInner_ThenInvalidatesExactKey()
    {
        var rule = MakeRule();

        await _sut.UpsertAsync(rule);

        Received.InOrder(() =>
        {
            _inner.UpsertAsync(rule, Arg.Any<CancellationToken>());
            _cache.RemoveAsync(TxStarCacheKey, CacheScope.Global, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task BulkUpsertAsync_WritesToInner_ThenBulkInvalidatesDistinctKeys()
    {
        var rules = new[]
        {
            MakeRule(ruleId: "R1", program: "STAR"),
            MakeRule(ruleId: "R2", program: "STAR"),       // dup key
            MakeRule(ruleId: "R3", program: "STARPlus")    // different key
        };

        await _sut.BulkUpsertAsync(rules);

        await _inner.Received(1).BulkUpsertAsync(Arg.Any<IEnumerable<PaRuleDocument>>(), Arg.Any<CancellationToken>());
        await _cache.Received(1).RemoveAsync(
            Arg.Is<IReadOnlyCollection<string>>(keys => keys.Count == 2),
            CacheScope.Global,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_FlushesStateKeys_ViaAnchoredScan()
    {
        var server   = Substitute.For<IServer>();
        var database = Substitute.For<IDatabase>();
        var endpoint = new DnsEndPoint("localhost", 6379);

        _multiplexer.GetEndPoints(Arg.Any<bool>()).Returns(new EndPoint[] { endpoint });
        _multiplexer.GetServer(endpoint, Arg.Any<object>()).Returns(server);
        _multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        server.Keys(
            Arg.Any<int>(),
            Arg.Any<RedisValue>(),
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<CommandFlags>())
            .Returns(new RedisKey[]
            {
                "test:_global:pa-rules:TX:3:STAR:txmco01",
                "test:_global:pa-rules:TX:3:any:platform"
            });

        await _sut.DeleteAsync("TX-STAR-REG-001", "TX");

        await _inner.Received(1).DeleteAsync("TX-STAR-REG-001", "TX", Arg.Any<CancellationToken>());

        // SCAN pattern is ANCHORED at the guard prefix — no leading wildcard.
        // Copilot flagged the previous "*pa-rules:…" pattern because it
        // forced SCAN to traverse the entire keyspace; the anchored form
        // keeps Redis's trie pruning intact. Keys() is synchronous on IServer.
        server.Received(1).Keys(
            Arg.Any<int>(),
            Arg.Is<RedisValue>(v => v.ToString() == "test:_global:pa-rules:TX:*"),
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<CommandFlags>());

        // SCAN path goes direct to multiplexer — bulk RemoveAsync on
        // ICacheProvider must NOT be called (it would double-prefix).
        await database.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2),
            Arg.Any<CommandFlags>());
        await _cache.DidNotReceive().RemoveAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CacheScope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WithoutMultiplexer_SkipsScanGracefully()
    {
        // When the cache backend is InMemory / Null, AddChoCaching does not
        // register IConnectionMultiplexer. RedisPaRuleRepository must still
        // construct — the SCAN path becomes a debug-logged no-op. Exact-key
        // invalidation on Upsert/BulkUpsert covers the common write path.
        var sutWithoutMux = new RedisPaRuleRepository(
            _inner,
            _cache,
            multiplexer: null,
            _keyGuard,
            Options.Create(new PriorAuthRuleEngineOptions { RuleSetCacheTtl = Ttl }),
            Substitute.For<ILogger<RedisPaRuleRepository>>());

        await sutWithoutMux.DeleteAsync("TX-STAR-REG-001", "TX");

        await _inner.Received(1).DeleteAsync("TX-STAR-REG-001", "TX", Arg.Any<CancellationToken>());
        // No multiplexer → no SCAN, no KeyDelete, no throw.
    }

    [Fact]
    public async Task ListAsync_BypassesCache()
    {
        var rules = new List<PaRuleDocument> { MakeRule() };
        _inner.ListAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(rules);

        var result = await _sut.ListAsync(tenantId: "txmco01", stateCode: "TX");

        result.Should().HaveCount(1);
        await _inner.Received(1).ListAsync("txmco01", "TX", Arg.Any<CancellationToken>());
    }

    private static PaRuleDocument MakeRule(
        string ruleId = "TX-STAR-REG-001",
        string stateCode = "TX",
        PaLineOfBusiness lob = PaLineOfBusiness.Medicaid,
        string? program = "STAR",
        string? tenantId = "txmco01") => new()
    {
        RuleId    = ruleId,
        RuleName  = $"Rule {ruleId}",
        StateCode = stateCode,
        Lob       = lob,
        Program   = program,
        TenantId  = tenantId,
        Category  = RuleCategory.RegulatoryExemption,
        Scope     = tenantId is null ? RuleScope.Platform : RuleScope.Tenant,
        Priority  = 1,
        RuleType  = "TxGoldCardExemption"
    };
}
