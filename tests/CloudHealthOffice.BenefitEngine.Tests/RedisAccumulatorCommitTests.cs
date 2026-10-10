using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// A real redis-server for <see cref="RedisAccumulatorCommitTests"/>:
/// <c>CHO_TEST_REDIS</c> (a connection string, as CI provides) or, when that is
/// unset, a <c>redis-server</c> on the PATH started on a free port, without
/// persistence. Neither available: the tests are skipped, never faked.
/// </summary>
public sealed class RedisServerFixture : IDisposable
{
    private readonly Process? _process;

    public ConnectionMultiplexer? Connection { get; }
    public string? Unavailable { get; }

    public RedisServerFixture()
    {
        var configured = Environment.GetEnvironmentVariable("CHO_TEST_REDIS");
        try
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                var port = FreePort();
                _process = Process.Start(new ProcessStartInfo("redis-server",
                    $"--port {port} --bind 127.0.0.1 --save \"\" --appendonly no")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                configured = $"127.0.0.1:{port}";
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    Connection = ConnectionMultiplexer.Connect(configured + ",allowAdmin=true,connectTimeout=1000");
                    break;
                }
                catch (RedisConnectionException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or RedisConnectionException)
        {
            Unavailable = $"No Redis for the accumulator store tests (set CHO_TEST_REDIS or install redis-server): {ex.Message}";
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Connection?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }
        _process?.Dispose();
    }
}

/// <summary>
/// <see cref="RedisAccumulatorService"/> — the store benefit-plan-service
/// runs with — on a real redis-server: the prepared-commit write the claims
/// pipeline makes when a claim finally passes, and the terminal fence a void
/// or denial sets.
/// </summary>
public sealed class RedisAccumulatorCommitTests : IClassFixture<RedisServerFixture>
{
    private static readonly Guid Plan = Guid.Parse("6f1c2b0e-3d4a-4b5c-8e9f-0a1b2c3d4e5f");
    private const string Year = "2026";
    private readonly RedisServerFixture _redis;
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
    private readonly FakeClaimsSource _source = new();

    public RedisAccumulatorCommitTests(RedisServerFixture redis) => _redis = redis;

    private RedisAccumulatorService Store()
    {
        Skip.If(_redis.Unavailable is not null, _redis.Unavailable);
        return new RedisAccumulatorService(_redis.Connection!, _source, new Tenant(_tenant),
            NullLogger<RedisAccumulatorService>.Instance);
    }

    private static AccumulatorCommit Commit(string claim, decimal deductible, string? replaces = null, decimal? limit = 500m) => new()
    {
        CommitId = Guid.NewGuid().ToString("N"),
        ClaimId = claim,
        ReplacesClaimId = replaces,
        MemberId = "M1",
        SubscriberId = "S1",
        BenefitPlanId = Plan,
        PlanYear = Year,
        Updates =
        [
            new AccumulatorUpdate { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Amount = deductible, Source = "Deductible", ClampAtLimit = limit },
            new AccumulatorUpdate { Type = AccumulatorType.IndividualOutOfPocketMax, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Amount = deductible, Source = "OOP" },
            new AccumulatorUpdate { Type = AccumulatorType.FamilyDeductible, Scope = AccumulatorScope.Family,
                NetworkTier = NetworkTier.InNetwork, Amount = deductible, Source = "Deductible" },
        ],
    };

    private static async Task<decimal> Read(IAccumulatorService store, AccumulatorType type)
    {
        var all = await store.GetAccumulatorsAsync("M1", "S1", Plan, Year);
        return all.Where(s => s.Type == type).Sum(s => s.AccumulatedAmountAfter);
    }

    /// <summary>Pricing reads (and so caches) the balances first, as the pipeline does.</summary>
    private async Task<RedisAccumulatorService> WarmStore(decimal priorDeductible = 0m)
    {
        _source.Individual = priorDeductible;
        var store = Store();
        Assert.Equal(priorDeductible, await Read(store, AccumulatorType.IndividualDeductible));
        return store;
    }

    [SkippableFact]
    public async Task Commit_AddsOnce_ARepeatIsANoOp()
    {
        var store = await WarmStore(priorDeductible: 40m);
        var commit = Commit("C1", 100m);

        Assert.Equal(AccumulatorCommitOutcome.Committed, await store.CommitAsync(commit));
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, await store.CommitAsync(commit));

        Assert.Equal(140m, await Read(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(100m, await Read(store, AccumulatorType.IndividualOutOfPocketMax));
        Assert.Equal(100m, await Read(store, AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task ANewCommitOfTheSameClaim_ReplacesItsEarlierOne()
    {
        var store = await WarmStore();
        await store.CommitAsync(Commit("C1", 100m));

        await store.CommitAsync(Commit("C1", 60m));

        Assert.Equal(60m, await Read(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(60m, await Read(store, AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task AReplacementsCommit_TakesTheReplacedClaimsCommitBack()
    {
        var store = await WarmStore();
        await store.CommitAsync(Commit("C1", 200m));

        await store.CommitAsync(Commit("C2", 300m, replaces: "C1"));

        Assert.Equal(300m, await Read(store, AccumulatorType.IndividualDeductible));
    }

    [SkippableFact]
    public async Task TheDeductible_IsClampedAtItsLimitAtWriteTime()
    {
        var store = await WarmStore(priorDeductible: 450m);

        await store.CommitAsync(Commit("C1", 100m, limit: 500m));

        Assert.Equal(500m, await Read(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(100m, await Read(store, AccumulatorType.IndividualOutOfPocketMax));
    }

    /// <summary>
    /// A denial or void fences the claim: its commit, arriving later, adds
    /// nothing — also after the cache was rebuilt from claims-service.
    /// </summary>
    [SkippableFact]
    public async Task AfterATerminalReversal_ACommitIsRefused_AlsoAfterARebuild()
    {
        var store = await WarmStore(priorDeductible: 40m);
        var late = Commit("C1", 100m);

        await store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1");
        // The next read rebuilds from claims-service (which does not count the denied claim).
        Assert.Equal(40m, await Read(store, AccumulatorType.IndividualDeductible));

        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, await store.CommitAsync(late));
        Assert.Equal(40m, await Read(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, await Read(store, AccumulatorType.FamilyDeductible));
    }

    /// <summary>A commit that lands first is taken out by the denial (the cache rebuilds without it).</summary>
    [SkippableFact]
    public async Task ACommitThatLandedFirst_IsGoneAfterTheTerminalReversal()
    {
        var store = await WarmStore(priorDeductible: 40m);
        await store.CommitAsync(Commit("C1", 100m));
        Assert.Equal(140m, await Read(store, AccumulatorType.IndividualDeductible));

        await store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1");

        Assert.Equal(40m, await Read(store, AccumulatorType.IndividualDeductible));
    }

    /// <summary>
    /// The hash was evicted or invalidated between pricing and the commit:
    /// nothing is added to a hash with no amounts (it would then look like the
    /// whole history); the next read rebuilds from claims-service.
    /// </summary>
    [SkippableFact]
    public async Task ACommitOnAColdCache_AddsNothing_TheRebuildCounts()
    {
        var store = Store();
        _source.Individual = 70m;

        Assert.Equal(AccumulatorCommitOutcome.Committed, await store.CommitAsync(Commit("C1", 100m)));

        Assert.Equal(70m, await Read(store, AccumulatorType.IndividualDeductible));
    }

    private sealed class Tenant(string id) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = id;
    }

    /// <summary>claims-service's accumulator-totals, as the rebuild reads them.</summary>
    private sealed class FakeClaimsSource : IClaimsAccumulatorSource
    {
        public decimal Individual { get; set; }

        public Task<(bool Success, IReadOnlyList<AccumulatorSnapshot> Snapshots)> CalculateAccumulatorsAsync(
            string tenantId, string ownerId, AccumulatorScope scope, Guid benefitPlanId, string planYear,
            CancellationToken ct = default)
        {
            IReadOnlyList<AccumulatorSnapshot> rows = scope == AccumulatorScope.Individual && Individual > 0
                ? [new AccumulatorSnapshot
                {
                    Type = AccumulatorType.IndividualDeductible, Scope = scope, NetworkTier = NetworkTier.InNetwork,
                    AccumulatedAmountAfter = Individual,
                }]
                : [];
            return Task.FromResult((true, rows));
        }
    }
}
