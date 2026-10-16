using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Testing.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// <see cref="RedisAccumulatorService"/> stores integer cents on a real
/// redis-server. HINCRBYFLOAT doubles drifted (10,000 × $0.10 is not
/// $1,000.00 in binary floating point), which can flip a deductible or
/// out-of-pocket met / not-met check. Hashes written before integer cents
/// are never read as cents, and accumulator audit rows are written before
/// the operation returns.
/// </summary>
public sealed class RedisAccumulatorCentsTests : IClassFixture<RedisServerFixture>
{
    private static readonly Guid Plan = Guid.Parse("0b6d3f9e-6a51-4c2e-9d7f-2a6c1e8b4f30");
    private const string Year = "2026";
    private readonly RedisServerFixture _redis;
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
    private readonly Source _source = new();

    public RedisAccumulatorCentsTests(RedisServerFixture redis) => _redis = redis;

    private RedisAccumulatorService Store(IAccumulatorAuditWriter? audit = null)
    {
        Skip.If(_redis.Unavailable is not null, _redis.Unavailable);
        return new RedisAccumulatorService(_redis.Connection!, _source, new Tenant(_tenant),
            NullLogger<RedisAccumulatorService>.Instance, audit);
    }

    private IDatabase Db => _redis.Connection!.GetDatabase();
    private RedisKey IndividualKey => $"accum:{_tenant}:IND:M1:{Plan}:{Year}";
    private const string DeductibleField = "IndividualDeductible:InNetwork";

    private static AccumulatorUpdate Deductible(decimal amount, decimal? limit = null) => new()
    {
        Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
        NetworkTier = NetworkTier.InNetwork, Amount = amount, Source = "Deductible", ClampAtLimit = limit,
    };

    private static AccumulatorCommit Commit(string claim, params AccumulatorUpdate[] updates) => new()
    {
        CommitId = Guid.NewGuid().ToString("N"),
        ClaimId = claim,
        MemberId = "M1",
        SubscriberId = "S1",
        BenefitPlanId = Plan,
        PlanYear = Year,
        Updates = updates.ToList(),
    };

    private static async Task<decimal> ReadDeductible(IAccumulatorService store) =>
        (await store.GetAccumulatorsAsync("M1", "S1", Plan, Year))
            .Where(s => s.Type == AccumulatorType.IndividualDeductible).Sum(s => s.AccumulatedAmountAfter);

    private async Task<RedisAccumulatorService> Warm(IAccumulatorAuditWriter? audit = null)
    {
        var store = Store(audit);
        Assert.Equal(_source.Individual, await ReadDeductible(store)); // pricing reads (and caches) first
        return store;
    }

    // ── Exact sums ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TenThousandTenCentIncrements_SumToExactlyOneThousandDollars()
    {
        var store = await Warm();

        for (var i = 0; i < 10_000; i++)
        {
            await store.ApplyUpdatesAsync("M1", "S1", Plan, Year, $"C{i}", [Deductible(0.10m)]);
        }

        Assert.Equal(1000.00m, await ReadDeductible(store));
        Assert.Equal("100000", (string?)await Db.HashGetAsync(IndividualKey, DeductibleField));
    }

    [SkippableFact]
    public async Task OneThousandTenCentCommits_SumToExactlyOneHundredDollars()
    {
        var store = await Warm();

        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(AccumulatorCommitOutcome.Committed, (await store.CommitAsync(Commit($"C{i}", Deductible(0.10m)))).Outcome);
        }

        Assert.Equal(100.00m, await ReadDeductible(store));
    }

    // ── Limits ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ADeductibleFilledToExactlyItsLimit_IsMet_AndNothingMoreFits()
    {
        var store = await Warm();

        // 166.67 + 166.67 + 166.66 = 500.00 exactly; in doubles it is not.
        foreach (var (claim, amount) in new[] { ("C1", 166.67m), ("C2", 166.67m), ("C3", 166.66m) })
        {
            var result = await store.CommitAsync(Commit(claim, Deductible(amount, limit: 500m)));
            Assert.Empty(result.Clamped);
        }

        Assert.Equal(500.00m, await ReadDeductible(store));

        var extra = await store.CommitAsync(Commit("C4", Deductible(0.01m, limit: 500m)));
        var clamp = Assert.Single(extra.Clamped);
        Assert.Equal(0.01m, clamp.Requested);
        Assert.Equal(0m, clamp.Applied);
        Assert.Equal(500.00m, await ReadDeductible(store));
    }

    [SkippableFact]
    public async Task APartialFitIsClampedToTheExactCentsOfRoomLeft()
    {
        var store = await Warm();
        await store.CommitAsync(Commit("C1", Deductible(499.99m, limit: 500m)));

        var result = await store.CommitAsync(Commit("C2", Deductible(25.37m, limit: 500m)));

        var clamp = Assert.Single(result.Clamped);
        Assert.Equal(25.37m, clamp.Requested);
        Assert.Equal(0.01m, clamp.Applied);
        Assert.Equal(500.00m, await ReadDeductible(store));
    }

    // ── Sub-cent input ──────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("0.005")]
    [InlineData("12.345")]
    [InlineData("-0.001")]
    public async Task SubCentAmounts_AreRejected_AndNothingIsWritten(string amountText)
    {
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        _source.Individual = 40m;
        var store = await Warm();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CommitAsync(Commit("C1", Deductible(amount))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyUpdatesAsync("M1", "S1", Plan, Year, "C2", [Deductible(amount)]));

        Assert.Equal(40m, await ReadDeductible(store));
        Assert.False(await Db.HashExistsAsync(IndividualKey, "__commit:C1"));
    }

    [Fact]
    public void ToCents_RoundTripsWholeCents_AndRejectsSubCent()
    {
        Assert.Equal(12345L, AccumulatorCents.ToCents(123.45m));
        Assert.Equal(-1L, AccumulatorCents.ToCents(-0.01m));
        Assert.Equal(123.45m, AccumulatorCents.FromCents(12345L));
        Assert.Throws<ArgumentException>(() => AccumulatorCents.ToCents(0.125m));
    }

    // ── Rebuild ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ARebuild_ProducesTheSameTotalsAndJournalsAsTheCommits()
    {
        var store = await Warm();
        var committed = new[] { ("C1", 123.45m), ("C2", 0.10m), ("C3", 876.44m), ("C4", 0.01m) };
        foreach (var (claim, amount) in committed)
            await store.CommitAsync(Commit(claim, Deductible(amount)));
        var beforeTotal = await ReadDeductible(store);
        var beforeJournals = new Dictionary<string, decimal>();
        foreach (var (claim, _) in committed)
            beforeJournals[claim] = (await store.GetClaimUpdatesAsync("M1", "S1", Plan, Year, claim)).Sum(u => u.Amount);

        // The system of record reports the same claims; the cache is evicted and rebuilt.
        _source.Individual = committed.Sum(c => c.Item2);
        _source.Claims = committed.Select(c => new ClaimAccumulatorContribution(c.Item1, [Deductible(c.Item2)])).ToList();
        await Db.KeyDeleteAsync(IndividualKey);

        Assert.Equal(1000.00m, beforeTotal);
        Assert.Equal(beforeTotal, await ReadDeductible(store));
        foreach (var (claim, _) in committed)
            Assert.Equal(beforeJournals[claim], (await store.GetClaimUpdatesAsync("M1", "S1", Plan, Year, claim)).Sum(u => u.Amount));
        Assert.Equal("100000", (string?)await Db.HashGetAsync(IndividualKey, DeductibleField));
    }

    // ── Hashes written before integer cents ─────────────────────────────

    /// <summary>A hash as HINCRBYFLOAT left it: dollars, a journal in dollars, a fence, no schema marker.</summary>
    private async Task SeedLegacyHashAsync()
    {
        await Db.HashSetAsync(IndividualKey, new HashEntry[]
        {
            new(DeductibleField, "750.00000000000011"),
            new("__commit:OLD", "commit-old|{\"" + DeductibleField + "\":{\"a\":750.00000000000011,\"t\":\"IndividualDeductible\",\"n\":\"InNetwork\",\"s\":\"Deductible\"}}"),
            new("__fence:VOIDED", "1"),
        });
    }

    [SkippableFact]
    public async Task ALegacyHash_IsNeverReadAsCents_ItIsRebuiltFromClaimsService()
    {
        await SeedLegacyHashAsync();
        _source.Individual = 750.00m;
        var store = Store();

        Assert.Equal(750.00m, await ReadDeductible(store));

        Assert.Equal("2", (string?)await Db.HashGetAsync(IndividualKey, "__v"));
        Assert.Equal("75000", (string?)await Db.HashGetAsync(IndividualKey, DeductibleField));
        Assert.False(await Db.HashExistsAsync(IndividualKey, "__commit:OLD"));
        Assert.Equal(1, _source.Calls);
    }

    [SkippableFact]
    public async Task ALegacyHash_KeepsItsFences()
    {
        await SeedLegacyHashAsync();
        _source.Individual = 750.00m;
        var store = Store();
        await ReadDeductible(store);

        var result = await store.CommitAsync(Commit("VOIDED", Deductible(10m)));

        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, result.Outcome);
        Assert.Equal(750.00m, await ReadDeductible(store));
    }

    [SkippableFact]
    public async Task ALegacyJournal_IsNotReturnedAsTheClaimsContribution()
    {
        await SeedLegacyHashAsync();
        var store = Store();

        Assert.Empty(await store.GetClaimUpdatesAsync("M1", "S1", Plan, Year, "OLD"));
    }

    [SkippableFact]
    public async Task ACommitOnALegacyHash_DropsTheDollars_AndLeavesItForTheRebuild()
    {
        await SeedLegacyHashAsync();
        var store = Store();

        Assert.Equal(AccumulatorCommitOutcome.Committed, (await store.CommitAsync(Commit("C1", Deductible(10m)))).Outcome);

        Assert.False(await Db.HashExistsAsync(IndividualKey, DeductibleField));
        _source.Individual = 760.00m; // claims-service counts C1 (it is Approved before it commits)
        Assert.Equal(760.00m, await ReadDeductible(store));
    }

    [SkippableFact]
    public async Task AnIncrementOnALegacyHash_IsNotAddedToTheDollars()
    {
        await SeedLegacyHashAsync();
        var store = Store();

        await store.ApplyUpdatesAsync("M1", "S1", Plan, Year, "C1", [Deductible(10m)]);

        Assert.False(await Db.HashExistsAsync(IndividualKey, DeductibleField));
        _source.Individual = 760.00m;
        Assert.Equal(760.00m, await ReadDeductible(store));
    }

    // ── Audit rows are written before the operation returns ─────────────

    [SkippableFact]
    public async Task Commit_WaitsForTheAuditRow()
    {
        var audit = new Audit { Gate = new TaskCompletionSource() };
        var store = await Warm(audit);

        var commit = store.CommitAsync(Commit("C1", Deductible(10m)));
        await Task.Delay(100);
        Assert.False(commit.IsCompleted);

        audit.Gate.SetResult();
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await commit).Outcome);
        Assert.Equal(["C1"], audit.Written);
    }

    [SkippableFact]
    public async Task Commit_WhoseAuditFails_Throws_AndItsRetryWritesTheAudit()
    {
        var audit = new Audit { FailNext = 1 };
        var store = await Warm(audit);
        var commit = Commit("C1", Deductible(10m));

        var ex = await Assert.ThrowsAsync<AccumulatorAuditException>(() => store.CommitAsync(commit));
        Assert.Equal("C1", ex.ClaimId);
        Assert.Empty(audit.Written);

        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, (await store.CommitAsync(commit)).Outcome);
        Assert.Equal(["C1"], audit.Written);
        Assert.Equal(10m, await ReadDeductible(store));
    }

    [SkippableFact]
    public async Task ApplyUpdates_WhoseAuditFails_Throws_BeforeAnythingIsAdded()
    {
        var audit = new Audit { FailNext = 1 };
        var store = await Warm(audit);

        await Assert.ThrowsAsync<AccumulatorAuditException>(() =>
            store.ApplyUpdatesAsync("M1", "S1", Plan, Year, "C1", [Deductible(10m)]));
        Assert.Equal(0m, await ReadDeductible(store));

        await store.ApplyUpdatesAsync("M1", "S1", Plan, Year, "C1", [Deductible(10m)]);
        Assert.Equal(10m, await ReadDeductible(store));
        Assert.Equal(["C1"], audit.Written);
    }

    [SkippableFact]
    public async Task Reversal_WhoseAuditFails_Throws()
    {
        var audit = new Audit { FailNext = 1 };
        var store = await Warm(audit);

        await Assert.ThrowsAsync<AccumulatorAuditException>(() =>
            store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1"));

        await store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1");
        Assert.Equal(["reverse:C1"], audit.Written);
    }

    // ── Fakes ───────────────────────────────────────────────────────────

    private sealed class Tenant(string id) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = id;
    }

    private sealed class Source : IClaimsAccumulatorSource
    {
        public decimal Individual { get; set; }
        public IReadOnlyList<ClaimAccumulatorContribution> Claims { get; set; } = [];
        public int Calls { get; private set; }

        public Task<(bool Success, IReadOnlyList<AccumulatorSnapshot> Snapshots)> CalculateAccumulatorsAsync(
            string tenantId, string ownerId, AccumulatorScope scope, Guid benefitPlanId, string planYear,
            CancellationToken ct = default)
        {
            if (scope == AccumulatorScope.Individual) Calls++;
            IReadOnlyList<AccumulatorSnapshot> rows = scope == AccumulatorScope.Individual && Individual > 0
                ? [new AccumulatorSnapshot
                {
                    Type = AccumulatorType.IndividualDeductible, Scope = scope, NetworkTier = NetworkTier.InNetwork,
                    AccumulatedAmountAfter = Individual,
                }]
                : [];
            return Task.FromResult((true, rows));
        }

        public async Task<AccumulatorRebuildData> CalculateAccumulatorsWithClaimsAsync(
            string tenantId, string ownerId, AccumulatorScope scope, Guid benefitPlanId, string planYear,
            CancellationToken ct = default)
        {
            var (success, rows) = await CalculateAccumulatorsAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct);
            return new AccumulatorRebuildData(success, rows, scope == AccumulatorScope.Individual ? Claims : []);
        }
    }

    private sealed class Audit : IAccumulatorAuditWriter
    {
        public TaskCompletionSource? Gate { get; init; }
        public int FailNext { get; set; }
        public List<string> Written { get; } = new();

        private async Task WriteAsync(string entry)
        {
            if (Gate is not null) await Gate.Task;
            if (FailNext > 0)
            {
                FailNext--;
                throw new TimeoutException("audit store unavailable");
            }
            if (!Written.Contains(entry)) Written.Add(entry); // idempotent per claim, as the Mongo writer is
        }

        public Task WriteAuditAsync(string tenantId, string memberId, string subscriberId, Guid benefitPlanId,
            string planYear, string claimId, IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
            => WriteAsync(claimId);

        public Task WriteReversalAuditAsync(string tenantId, string memberId, string subscriberId, Guid benefitPlanId,
            string planYear, string claimId, CancellationToken ct = default)
            => WriteAsync("reverse:" + claimId);
    }
}
