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
/// PR #1278 follow-ups, over the real Mongo stores (EphemeralMongo):
/// <list type="bullet">
///   <item><description>2: an apply that stalls past its lease while a replacement
///   or void takes the marker over and tombstones it must not count when it
///   resumes (the tombstone bumps the snapshot version, the apply re-checks
///   its lease, and its completion is conditional on the lease).</description></item>
///   <item><description>3: a void of the original is not a replacement: the first
///   replacement after it counts; a second replacement is still skipped.</description></item>
/// </list>
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorCobFollowUpTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Member = "m-500";
    private static readonly DateTime YearStart = new(2026, 1, 1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private IMongoDatabase _db = null!;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly RecordingPublisher _pub = new();
    private HookedRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("accumulators_followups");
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        _repo = new HookedRepository(new AccumulatorRepositoryMongo(_db));
        await _repo.TryReplaceSnapshotAsync(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(Tenant, Member, YearStart),
            TenantId = Tenant,
            MemberId = Member,
            PlanYearStart = YearStart,
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = 2000m,
            IndividualOopLimit = 8000m,
        }, expectedVersion: 0);
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ProcessedClaimStoreMongo Store() => new(_db, Lease, _clock);

    private Svc Service() => new(_repo, Store(), _pub, NullLogger<Svc>.Instance);

    private async Task<AccumulatorSnapshot> Snapshot() =>
        (await _repo.GetSnapshotAsync(Tenant, Member, YearStart))!;

    private static ClaimFinalizedEvent Claim(
        string claimId, decimal deductible, string status = "Paid", string? frequency = null, string? original = null) => new()
    {
        TenantId = Tenant,
        ClaimId = claimId,
        ClaimNumber = claimId,
        MemberId = Member,
        ServiceDate = new DateTime(2026, 3, 15),
        BenefitCategory = "OV",
        FinalStatus = status,
        ClaimFrequencyCode = frequency,
        OriginalClaimId = original,
        DeductibleApplied = deductible,
        OopApplied = deductible,
        MemberResponsibility = deductible,
    };

    // ── follow-up 2 ───────────────────────────────────────────────────

    /// <summary>
    /// C1's apply stalls for more than the lease just before its append. C2
    /// (replacing C1) takes C1's stale marker over, tombstones it and applies
    /// 200. C1 then resumes and appends 100: it used to leave the deductible
    /// at 300. Now its append conflicts, its re-read sees the lost lease and
    /// it stops: 200.
    /// </summary>
    [Fact]
    public async Task StalledApply_ResumingAfterAReplacementTombstonedIt_DoesNotCount()
    {
        ApplyResult? c2 = null;
        _repo.BeforeNextAppend = async row =>
        {
            Assert.Equal("C1", row.SourceClaimId); // C1's apply, about to append
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
            c2 = await Service().ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));
        };

        var c1 = await Service().ApplyClaimFinalizedAsync(Claim("C1", 100m));

        Assert.Equal(ApplyOutcome.Applied, c2!.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        Assert.Equal(Svc.ReversedBeforeApplyOutcome, c1.Reason);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);
        Assert.Null(await _repo.GetClaimAppliedEventAsync(Tenant, "C1"));
        // The tombstone survives: C1's apply did not overwrite it.
        Assert.Equal(Svc.ReversedBeforeApplyOutcome, (await Store().GetAsync(Tenant, "C1"))!.Outcome);
    }

    /// <summary>
    /// The same stall, but the original is voided (frequency 8) — nothing else
    /// writes the snapshot. Without the tombstone's version bump the stalled
    /// append at the version it read succeeded and counted 100 for a voided claim.
    /// </summary>
    [Fact]
    public async Task StalledApply_ResumingAfterAVoidTombstonedIt_DoesNotCount()
    {
        ApplyResult? v1 = null;
        _repo.BeforeNextAppend = async _ =>
        {
            _clock.Advance(Lease + TimeSpan.FromSeconds(1));
            v1 = await Service().ApplyClaimFinalizedAsync(Claim("V1", 100m, status: "Reversed", frequency: "8", original: "C1"));
        };

        var c1 = await Service().ApplyClaimFinalizedAsync(Claim("C1", 100m));

        Assert.Equal(Svc.ReversedBeforeApplyOutcome, v1!.Reason);
        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        var snapshot = await Snapshot();
        Assert.Equal(0m, snapshot.IndividualDeductibleUsed);
        var rows = await _repo.GetAggregateEventsAsync(Tenant, snapshot.Id);
        var tombstone = Assert.Single(rows);
        Assert.Equal(Svc.ClaimTombstonedEventType, tombstone.EventType);
        Assert.Equal(0m, tombstone.DeductibleDelta);
        Assert.Equal(ReversalKinds.Void, tombstone.ReversalKind);
    }

    /// <summary>
    /// The other order: the replacement has taken C1's stale lease over, and
    /// the stalled apply's row lands just before the tombstone row, at the
    /// same version. The tombstone row conflicts; the replacement finds C1's
    /// row and reverses it instead: C1 never counts on top of C2.
    /// </summary>
    [Fact]
    public async Task StalledApplysRowLandsBeforeTheTombstoneRow_IsReversed()
    {
        var store = Store();
        var lease = await store.BeginLeaseAsync(Tenant, "C1");
        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var snapshot = await Snapshot();
        _repo.BeforeNextAppend = async row =>
        {
            Assert.Equal(Svc.ClaimTombstonedEventType, row.EventType);
            Assert.True(await _repo.TryAppendEventAsync(new AccumulatorEvent
            {
                Id = AccumulatorEvent.BuildId(snapshot.Id, 1), TenantId = Tenant, AggregateId = snapshot.Id, Version = 1,
                MemberId = Member, PlanYearStart = YearStart, PlanYearEnd = snapshot.PlanYearEnd,
                EventType = "ClaimApplied", SourceClaimId = "C1", SourceReference = "C1",
                DeductibleDelta = 100m, OopDelta = 100m, DeltasClamped = true,
            }));
        };

        var c2 = await Service().ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.Applied, c2.Outcome);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);
        Assert.NotNull(await _repo.GetClaimReversedEventAsync(Tenant, "C1"));
        // The stalled worker can no longer complete C1's marker.
        Assert.False(await store.CompleteLeaseAsync(Tenant, "C1", lease.Token!, "e", "Applied"));
    }

    [Fact]
    public async Task CompleteLease_IsConditionalOnTheLease()
    {
        var store = Store();
        var first = await store.BeginLeaseAsync(Tenant, "C9");
        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var second = await store.BeginLeaseAsync(Tenant, "C9");

        Assert.Equal(BeginClaimOutcome.Proceed, second.Outcome);
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(await store.CompleteLeaseAsync(Tenant, "C9", first.Token!, "e1", "Applied"));
        Assert.True(await store.CompleteLeaseAsync(Tenant, "C9", second.Token!, "e2", "Applied"));
        Assert.False(await store.CompleteLeaseAsync(Tenant, "C9", second.Token!, "e3", "Other"));
        var marker = await store.GetAsync(Tenant, "C9");
        Assert.Equal(("e2", "Applied"), (marker!.ResultingEventId, marker.Outcome));
    }

    // ── follow-up 3 ───────────────────────────────────────────────────

    /// <summary>
    /// C1 applied; V1 voids it (frequency 8); C2 then replaces C1. The void's
    /// ClaimReversed row names V1, and C2 used to be skipped as a "second
    /// replacement" (ded 0, reported as an orphan). It counts.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VoidThenReplacement_CountsTheReplacement(bool originalApplied)
    {
        var sut = Service();
        if (originalApplied) await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await sut.ApplyClaimFinalizedAsync(Claim("V1", 100m, status: "Reversed", frequency: "8", original: "C1"));

        var c2 = await sut.ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.Applied, c2.Outcome);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);
        Assert.DoesNotContain(_pub.Orphans, o => o.ClaimId == "C2");

        // A further replacement of C1 is a second replacement: skipped.
        var c3 = await sut.ApplyClaimFinalizedAsync(Claim("C3", 250m, frequency: "7", original: "C1"));
        Assert.Equal(Svc.SecondReplacementOutcome, c3.Reason);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);

        // C2's redelivery is a duplicate, not a second replacement.
        var again = await sut.ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));
        Assert.Equal(ApplyOutcome.Duplicate, again.Outcome);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);
    }

    [Fact]
    public async Task ReplacementThenSecondReplacement_IsStillSkipped()
    {
        var sut = Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await sut.ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));

        var c3 = await sut.ApplyClaimFinalizedAsync(Claim("C3", 250m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.Skipped, c3.Outcome);
        Assert.Equal(Svc.SecondReplacementOutcome, c3.Reason);
        Assert.Equal(200m, (await Snapshot()).IndividualDeductibleUsed);
        Assert.Contains(_pub.Orphans, o => o.ClaimId == "C3");
        Assert.Equal(ReversalKinds.Replacement, (await _repo.GetClaimReversedEventAsync(Tenant, "C1"))!.ReversalKind);
    }

    /// <summary>
    /// A row written before the kind was recorded keeps the earlier rule (a
    /// reversal naming another claim is a replacement): conservative — a
    /// possible second replacement is reported rather than double-counted.
    /// </summary>
    [Fact]
    public async Task LegacyReversalRowWithoutKind_IsStillTreatedAsAReplacement()
    {
        var sut = Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await sut.ApplyClaimFinalizedAsync(Claim("C2", 200m, frequency: "7", original: "C1"));
        var events = _db.GetCollection<AccumulatorEvent>(AccumulatorMongoIndexes.EventsCollection);
        await events.UpdateOneAsync(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimReversed"),
            Builders<AccumulatorEvent>.Update.Unset(e => e.ReversalKind));

        var c3 = await sut.ApplyClaimFinalizedAsync(Claim("C3", 250m, frequency: "7", original: "C1"));

        Assert.Equal(Svc.SecondReplacementOutcome, c3.Reason);
    }

    /// <summary>Delegates to the Mongo repository; runs a hook once before the next event append.</summary>
    private sealed class HookedRepository(IAccumulatorRepository inner) : IAccumulatorRepository
    {
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
