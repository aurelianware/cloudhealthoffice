using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Svc = AccumulatorService.Services.AccumulatorService;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// A frequency-7 replacement that resolves to a different snapshot than its
/// original — another plan year, or another member — over the real Mongo
/// stores (EphemeralMongo). The original's amounts are backed out of the
/// original's own snapshot, the replacement's counted on its own, and the
/// fence against a stalled original append (the zero-delta tombstone row) is
/// put on the original's snapshot.
/// <para>Deductible: the original C1 applied 200 to plan year 2025 (on top
/// of 50 from an earlier claim); its replacement C2 is for 2026 and applies
/// 300. Right: 2025 back to 50, 2026 = 300. Before the fix, in the stalled
/// interleaving, the tombstone went on 2026 and C1's stalled append still
/// landed on 2025: 250.</para>
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorSnapshotChangeTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string MemberA = "m-a";
    private const string MemberB = "m-b";
    private static readonly DateTime Py1 = new(2025, 1, 1);
    private static readonly DateTime Py2 = new(2026, 1, 1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private IMongoDatabase _db = null!;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private HookedRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("accumulators_snapshot_change");
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        _repo = new HookedRepository(new AccumulatorRepositoryMongo(_db));
        foreach (var (member, start) in new[] { (MemberA, Py1), (MemberA, Py2), (MemberB, Py2) })
        {
            await _repo.TryReplaceSnapshotAsync(new AccumulatorSnapshot
            {
                Id = AccumulatorSnapshot.BuildId(Tenant, member, start),
                TenantId = Tenant,
                MemberId = member,
                PlanYearStart = start,
                PlanYearEnd = start.AddYears(1).AddDays(-1),
                IndividualDeductibleLimit = 2000m,
                IndividualOopLimit = 8000m,
            }, expectedVersion: 0);
        }
        // Something else already counted 50 in 2025.
        await Service().ApplyClaimFinalizedAsync(Claim("C0", MemberA, new DateTime(2025, 2, 1), 50m));
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ProcessedClaimStoreMongo Store() => new(_db, Lease, _clock);

    private Svc Service() => new(_repo, Store(), new RecordingPublisher(), NullLogger<Svc>.Instance);

    private async Task<decimal> Deductible(string member, DateTime planYear) =>
        (await _repo.GetSnapshotAsync(Tenant, member, planYear))!.IndividualDeductibleUsed;

    private static ClaimFinalizedEvent Claim(
        string claimId, string member, DateTime serviceDate, decimal deductible,
        string? frequency = null, string? original = null) => new()
    {
        TenantId = Tenant,
        ClaimId = claimId,
        ClaimNumber = claimId,
        MemberId = member,
        ServiceDate = serviceDate,
        BenefitCategory = "OV",
        FinalStatus = "Paid",
        ClaimFrequencyCode = frequency,
        OriginalClaimId = original,
        DeductibleApplied = deductible,
        OopApplied = deductible,
        MemberResponsibility = deductible,
    };

    private static ClaimFinalizedEvent C1 => Claim("C1", MemberA, new DateTime(2025, 12, 20), 200m);

    private static ClaimFinalizedEvent C2(string member = MemberA) =>
        Claim("C2", member, new DateTime(2026, 1, 5), 300m, frequency: "7", original: "C1");

    // ── in order ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReplacementInAnotherPlanYear_ReversesTheOriginalOnItsOwnYear()
    {
        await Service().ApplyClaimFinalizedAsync(C1);
        Assert.Equal(250m, await Deductible(MemberA, Py1));

        var c2 = await Service().ApplyClaimFinalizedAsync(C2());

        Assert.Equal(ApplyOutcome.Applied, c2.Outcome);
        Assert.Equal(50m, await Deductible(MemberA, Py1));
        Assert.Equal(300m, await Deductible(MemberA, Py2));
        var reversal = (await _repo.GetClaimReversedEventAsync(Tenant, "C1"))!;
        Assert.Equal(AccumulatorSnapshot.BuildId(Tenant, MemberA, Py1), reversal.AggregateId);
        Assert.Equal(ReversalKinds.Replacement, reversal.ReversalKind);
    }

    [Fact]
    public async Task ReplacementForAnotherMember_ReversesTheOriginalOnItsOwnMember()
    {
        var c1 = Claim("C1", MemberA, new DateTime(2026, 3, 1), 200m);
        await Service().ApplyClaimFinalizedAsync(c1);

        await Service().ApplyClaimFinalizedAsync(C2(MemberB));

        Assert.Equal(0m, await Deductible(MemberA, Py2));
        Assert.Equal(300m, await Deductible(MemberB, Py2));
    }

    /// <summary>The replacement overtakes its original (another Kafka partition): the original never counts.</summary>
    [Fact]
    public async Task ReplacementInAnotherPlanYear_ArrivingFirst_TheOriginalNeverCounts()
    {
        await Service().ApplyClaimFinalizedAsync(C2());
        var c1 = await Service().ApplyClaimFinalizedAsync(C1);

        Assert.Equal(Svc.ReversedBeforeApplyOutcome, c1.Reason);
        Assert.Equal(50m, await Deductible(MemberA, Py1));
        Assert.Equal(300m, await Deductible(MemberA, Py2));
    }

    // ── a stalled original append ─────────────────────────────────────

    /// <summary>
    /// C1's apply stalls past its lease just before appending to 2025. C2
    /// (2026) takes C1's marker over, tombstones it and applies. C1 resumes.
    /// The zero-delta fence row is appended to 2025 — the snapshot C1's apply
    /// recorded on its marker — so C1's append conflicts and it stops. Before,
    /// the fence went on C2's 2026 snapshot and C1's append landed: 2025 = 250.
    /// </summary>
    [Fact]
    public async Task StalledOriginalAppend_InAnotherPlanYear_IsFencedOnTheOriginalsYear()
    {
        ApplyResult? c2 = null;
        _repo.BeforeNextAppend = async row =>
        {
            Assert.Equal("C1", row.SourceClaimId);
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
            c2 = await Service().ApplyClaimFinalizedAsync(C2());
        };

        var c1 = await Service().ApplyClaimFinalizedAsync(C1);

        Assert.Equal(ApplyOutcome.Applied, c2!.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        Assert.Equal(Svc.ReversedBeforeApplyOutcome, c1.Reason);
        Assert.Equal(50m, await Deductible(MemberA, Py1));
        Assert.Equal(300m, await Deductible(MemberA, Py2));
        Assert.Null(await _repo.GetClaimAppliedEventAsync(Tenant, "C1"));
        var py1Rows = await _repo.GetAggregateEventsAsync(Tenant, AccumulatorSnapshot.BuildId(Tenant, MemberA, Py1));
        var fence = Assert.Single(py1Rows, r => r.EventType == Svc.ClaimTombstonedEventType);
        Assert.Equal("C1", fence.SourceClaimId);
        Assert.Equal(ReversalKinds.Replacement, fence.ReversalKind);
        Assert.DoesNotContain(
            await _repo.GetAggregateEventsAsync(Tenant, AccumulatorSnapshot.BuildId(Tenant, MemberA, Py2)),
            r => r.EventType == Svc.ClaimTombstonedEventType);
    }

    /// <summary>
    /// The other order: the stalled C1 row lands on 2025 just before C2's
    /// fence row, at the same version. The fence conflicts; C2 finds C1's row
    /// and reverses it on 2025. Counted once either way.
    /// </summary>
    [Fact]
    public async Task StalledOriginalRowLandsBeforeTheFence_IsReversedOnTheOriginalsYear()
    {
        ApplyResult? c2 = null;
        _repo.BeforeNextAppend = async c1Row =>
        {
            Assert.Equal("C1", c1Row.SourceClaimId);
            _repo.BeforeNextAppend = async fenceRow =>
            {
                Assert.Equal(Svc.ClaimTombstonedEventType, fenceRow.EventType);
                Assert.Equal(c1Row.AggregateId, fenceRow.AggregateId);
                Assert.True(await _repo.Inner.TryAppendEventAsync(c1Row)); // the stalled append wins the version
            };
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
            c2 = await Service().ApplyClaimFinalizedAsync(C2());
        };

        await Service().ApplyClaimFinalizedAsync(C1);

        Assert.Equal(ApplyOutcome.Applied, c2!.Outcome);
        Assert.Equal(50m, await Deductible(MemberA, Py1));
        Assert.Equal(300m, await Deductible(MemberA, Py2));
        Assert.Equal(AccumulatorSnapshot.BuildId(Tenant, MemberA, Py1),
            (await _repo.GetClaimReversedEventAsync(Tenant, "C1"))!.AggregateId);
    }

    /// <summary>The same stall when the replacement is for another member.</summary>
    [Fact]
    public async Task StalledOriginalAppend_ReplacementForAnotherMember_IsFencedOnTheOriginalsMember()
    {
        var c1 = Claim("C1", MemberA, new DateTime(2026, 3, 1), 200m);
        _repo.BeforeNextAppend = async _ =>
        {
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
            await Service().ApplyClaimFinalizedAsync(C2(MemberB));
        };

        await Service().ApplyClaimFinalizedAsync(c1);

        Assert.Equal(0m, await Deductible(MemberA, Py2));
        Assert.Equal(300m, await Deductible(MemberB, Py2));
    }

    /// <summary>
    /// The apply records its target only under its lease: once a reversal
    /// has taken the lease over, a stalled apply cannot record (and so cannot
    /// append).
    /// </summary>
    [Fact]
    public async Task RecordLeaseTarget_IsConditionalOnTheLease()
    {
        var store = Store();
        var first = await store.BeginLeaseAsync(Tenant, "C9");
        var target = new LeaseTarget(AccumulatorSnapshot.BuildId(Tenant, MemberA, Py1), MemberA, Py1, Py1.AddYears(1).AddDays(-1));
        Assert.True(await store.RecordLeaseTargetAsync(Tenant, "C9", first.Token!, target));
        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var second = await store.BeginLeaseAsync(Tenant, "C9");

        Assert.False(await store.RecordLeaseTargetAsync(Tenant, "C9", first.Token!, target with { PlanYearStart = Py2 }));
        var marker = (await store.GetAsync(Tenant, "C9"))!;
        Assert.Equal(target, LeaseTarget.From(marker));
        Assert.True(await store.CompleteLeaseAsync(Tenant, "C9", second.Token!, "e", "Applied"));
        Assert.False(await store.RecordLeaseTargetAsync(Tenant, "C9", second.Token!, target));
    }

    /// <summary>Delegates to the Mongo repository; runs a hook once before the next event append.</summary>
    private sealed class HookedRepository(IAccumulatorRepository inner) : IAccumulatorRepository
    {
        public IAccumulatorRepository Inner => inner;
        public Func<AccumulatorEvent, Task>? BeforeNextAppend { get; set; }

        public async Task<bool> TryAppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
        {
            if (BeforeNextAppend is { } hook)
            {
                BeforeNextAppend = null;
                await hook(evt);
            }
            return await inner.TryAppendEventAsync(evt, ct);
        }

        public Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default) =>
            inner.GetSnapshotAsync(tenantId, memberId, planYearStart, ct);
        public Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default) =>
            inner.GetSnapshotByAsOfDateAsync(tenantId, memberId, asOfDate, ct);
        public Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default) =>
            inner.GetSnapshotsAsync(tenantId, memberId, ct);
        public Task<bool> TryReplaceSnapshotAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct = default) =>
            inner.TryReplaceSnapshotAsync(snapshot, expectedVersion, ct);
        public Task<IReadOnlyList<AccumulatorEvent>> GetAggregateEventsAsync(string tenantId, string aggregateId, long afterVersion = 0, CancellationToken ct = default) =>
            inner.GetAggregateEventsAsync(tenantId, aggregateId, afterVersion, ct);
        public Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default) =>
            inner.GetEventsAsync(tenantId, memberId, take, ct);
        public Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default) =>
            inner.GetManualAdjustmentAsync(tenantId, adjustmentId, ct);
        public Task<AccumulatorEvent?> GetClaimAppliedEventAsync(string tenantId, string claimId, CancellationToken ct = default) =>
            inner.GetClaimAppliedEventAsync(tenantId, claimId, ct);
        public Task<AccumulatorEvent?> GetClaimReversedEventAsync(string tenantId, string claimId, CancellationToken ct = default) =>
            inner.GetClaimReversedEventAsync(tenantId, claimId, ct);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
