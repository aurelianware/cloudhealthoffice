using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// Store guards on the real Mongo stores (EphemeralMongo), ported from the
/// removed Cosmos emulator suite: a void backed out with a Void reversal row, the
/// replacement tombstone's reversal kind, a concurrent takeover of an expired
/// lease, lease-token-conditional completion, release of a Pending marker
/// only, concurrent version-conditional snapshot writes, one event row per
/// (snapshot, version), the event and snapshot queries, and tenant isolation.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorMongoStoreGuardTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Member = "m-400";
    private static readonly DateTime YearStart = new(2026, 1, 1);
    private IMongoDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("accumulator_guards");
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        await Repo().TryReplaceSnapshotAsync(new AccumulatorSnapshot
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

    private AccumulatorRepositoryMongo Repo() => new(_db);

    private global::AccumulatorService.Services.AccumulatorService Service(IProcessedClaimStore? store = null) =>
        new(Repo(), store ?? new ProcessedClaimStoreMongo(_db), new RecordingPublisher(),
            NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);

    private async Task<AccumulatorSnapshot> Snapshot() =>
        (await Repo().GetSnapshotAsync(Tenant, Member, YearStart))!;

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

    // ── Service scenarios ────────────────────────────────────────────────

    [Fact]
    public async Task ApplyThenVoid_CountsOnce_AndBacksItOut_WithAVoidReversalRow()
    {
        var sut = Service();

        Assert.Equal(ApplyOutcome.Applied, (await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m))).Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, (await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m))).Outcome);
        Assert.Equal(300m, (await Snapshot()).IndividualDeductibleUsed);

        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed"));
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed")); // replay

        Assert.Equal(0m, (await Snapshot()).IndividualDeductibleUsed);
        var reversal = await Repo().GetClaimReversedEventAsync(Tenant, "C1");
        Assert.NotNull(reversal);
        Assert.Equal(ReversalKinds.Void, reversal!.ReversalKind);
        Assert.Equal(-300m, reversal.DeductibleDelta);
        Assert.Single(await Repo().GetEventsAsync(Tenant, Member), e => e.EventType == "ClaimReversed");
        var applied = await Repo().GetClaimAppliedEventAsync(Tenant, "C1");
        Assert.Equal(300m, applied!.DeductibleDelta);
    }

    /// <summary>The reversal that arrived before its original is held as a Replacement tombstone.</summary>
    [Fact]
    public async Task ReplacementBeforeOriginal_HoldsAReplacementTombstone()
    {
        var sut = Service();

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m));

        var tombstone = await new ProcessedClaimStoreMongo(_db).GetAsync(Tenant, "C1:reversal");
        Assert.NotNull(tombstone);
        Assert.Equal(ReversalKinds.Replacement, tombstone!.ReversalKind);
        Assert.Equal(300m, (await Snapshot()).IndividualDeductibleUsed);
    }

    // ── Processed-claim markers: leases ──────────────────────────────────

    /// <summary>
    /// Many consumers retry one expired marker at the same moment: the
    /// conditional takeover lets exactly one of them take it over.
    /// </summary>
    [Fact]
    public async Task ExpiredLease_ConcurrentTakeover_HasExactlyOneWinner()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreMongo(_db, TimeSpan.FromMinutes(2), clock);
        await store.BeginLeaseAsync(Tenant, "C7");
        clock.Advance(TimeSpan.FromMinutes(3));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.BeginLeaseAsync(Tenant, "C7")));

        var winners = attempts.Where(a => a.Outcome == BeginClaimOutcome.Proceed).ToList();
        Assert.Single(winners);
        Assert.All(attempts.Except(winners), a => Assert.Equal(BeginClaimOutcome.InProgress, a.Outcome));
        Assert.Equal(winners[0].Token, (await store.GetAsync(Tenant, "C7"))!.LeaseToken);
    }

    /// <summary>
    /// An attempt whose lease was taken over cannot complete the marker: the
    /// new holder's outcome (here a reversal tombstone with its kind) stands.
    /// </summary>
    [Fact]
    public async Task CompleteLease_IsConditionalOnTheLeaseToken_AndKeepsTheReversalKind()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreMongo(_db, TimeSpan.FromMinutes(2), clock);
        var stale = await store.BeginLeaseAsync(Tenant, "C5:reversal");
        clock.Advance(TimeSpan.FromMinutes(3));
        var fresh = await store.BeginLeaseAsync(Tenant, "C5:reversal");
        Assert.Equal(BeginClaimOutcome.Proceed, fresh.Outcome);
        Assert.NotEqual(stale.Token, fresh.Token);

        Assert.False(await store.CompleteLeaseAsync(Tenant, "C5:reversal", stale.Token!, "ev-stale", "Reversed", ReversalKinds.Denial));
        Assert.True(await store.CompleteLeaseAsync(Tenant, "C5:reversal", fresh.Token!, "C6", "ReversedBeforeApply", ReversalKinds.Void));
        Assert.False(await store.CompleteLeaseAsync(Tenant, "C5:reversal", fresh.Token!, "ev-again", "Reversed"), "already terminal");
        Assert.False(await store.CompleteLeaseAsync(Tenant, "missing", fresh.Token!, "x", "Applied"));

        var marker = (await store.GetAsync(Tenant, "C5:reversal"))!;
        Assert.Equal("ReversedBeforeApply", marker.Outcome);
        Assert.Equal("C6", marker.ResultingEventId);
        Assert.Equal(ReversalKinds.Void, marker.ReversalKind);
        Assert.Equal(BeginClaimOutcome.AlreadyApplied, await store.TryBeginAsync(Tenant, "C5:reversal"));
    }

    [Fact]
    public async Task Release_DeletesOnlyAPendingMarker()
    {
        var store = new ProcessedClaimStoreMongo(_db);
        await store.BeginLeaseAsync(Tenant, "C3");
        await store.ReleaseAsync(Tenant, "C3");
        Assert.Null(await store.GetAsync(Tenant, "C3"));
        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(Tenant, "C3"));

        await store.ReleaseAsync(Tenant, "never-begun"); // no throw

        // A marker completed under its lease is terminal: a later release keeps it.
        var lease = await store.BeginLeaseAsync(Tenant, "C4");
        Assert.True(await store.CompleteLeaseAsync(Tenant, "C4", lease.Token!, "ev-1", "Applied"));
        await store.ReleaseAsync(Tenant, "C4");
        Assert.Equal("Applied", (await store.GetAsync(Tenant, "C4"))!.Outcome);
    }

    // ── Snapshot and event rows ──────────────────────────────────────────

    [Fact]
    public async Task SnapshotWrite_CreateConflictsOnTheId_AndAnUnknownVersionIsRefused()
    {
        var repo = Repo();
        var duplicate = await Snapshot();
        duplicate.Id = AccumulatorSnapshot.BuildId(Tenant, "m-new", YearStart);
        duplicate.MemberId = "m-new";
        duplicate.Version = 1; // a claim's first write: the stored row is then past version 0

        Assert.True(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 0));
        Assert.False(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 0), "a second creator conflicts");
        Assert.False(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 7), "no such version");
    }

    /// <summary>
    /// Two writers both read version N and both try to write N+1: the
    /// version-conditional replace lets exactly one through.
    /// </summary>
    [Fact]
    public async Task SnapshotWrite_ConcurrentWritersAtOneVersion_HaveExactlyOneWinner()
    {
        var writers = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            var snap = await Snapshot();
            snap.Version = 1;
            snap.IndividualDeductibleUsed = 100m + i;
            return await Repo().TryReplaceSnapshotAsync(snap, expectedVersion: 0);
        }));

        Assert.Single(writers, w => w);
        Assert.Equal(1, (await Snapshot()).Version);
    }

    [Fact]
    public async Task EventAppend_IsOneRowPerSnapshotVersion_AndTheQueriesFindTheRows()
    {
        var repo = Repo();
        var aggregate = AccumulatorSnapshot.BuildId(Tenant, Member, YearStart);
        AccumulatorEvent Row(long version, string type = "ClaimApplied", string? claim = "C1") => new()
        {
            Id = AccumulatorEvent.BuildId(aggregate, version), TenantId = Tenant, AggregateId = aggregate,
            Version = version, MemberId = Member, PlanYearStart = YearStart, EventType = type,
            SourceClaimId = claim, SourceReference = claim, DeductibleDelta = 10m * version,
            OccurredAt = new DateTime(2026, 3, 1).AddMinutes(version),
        };

        Assert.True(await repo.TryAppendEventAsync(Row(1)));
        Assert.False(await repo.TryAppendEventAsync(Row(1, claim: "C-other")), "a second writer at v1 conflicts");
        Assert.True(await repo.TryAppendEventAsync(Row(2, "ClaimReversed")));
        Assert.True(await repo.TryAppendEventAsync(Row(3, global::AccumulatorService.Services.AccumulatorService.ClaimTombstonedEventType, "C2")));
        Assert.True(await repo.TryAppendEventAsync(Row(4, "ManualAdjustment", "ADJ-1")));

        Assert.Equal(new long[] { 2, 3, 4 }, (await repo.GetAggregateEventsAsync(Tenant, aggregate, afterVersion: 1)).Select(e => e.Version));
        Assert.Equal(new long[] { 4, 3 }, (await repo.GetEventsAsync(Tenant, Member, take: 2)).Select(e => e.Version));
        Assert.Equal(2, (await repo.GetClaimReversedEventAsync(Tenant, "C1"))!.Version);
        Assert.Null(await repo.GetClaimReversedEventAsync(Tenant, "C2"));
        Assert.Equal(1, (await repo.GetClaimAppliedEventAsync(Tenant, "C1"))!.Version);
        Assert.Equal(4, (await repo.GetManualAdjustmentAsync(Tenant, "ADJ-1"))!.Version);

        // Tenant isolation: another tenant sees none of these rows.
        Assert.Empty(await repo.GetAggregateEventsAsync("other-tenant", aggregate));
        Assert.Empty(await repo.GetEventsAsync("other-tenant", Member));
        Assert.Null(await repo.GetClaimAppliedEventAsync("other-tenant", "C1"));
        Assert.Null(await repo.GetSnapshotAsync("other-tenant", Member, YearStart));
    }

    [Fact]
    public async Task SnapshotQueries_FindThePlanYear()
    {
        var repo = Repo();
        var prior = await Snapshot();
        prior.Id = AccumulatorSnapshot.BuildId(Tenant, Member, new DateTime(2025, 1, 1));
        prior.PlanYearStart = new DateTime(2025, 1, 1);
        prior.PlanYearEnd = new DateTime(2025, 12, 31);
        Assert.True(await repo.TryReplaceSnapshotAsync(prior, expectedVersion: 0));

        Assert.Equal(YearStart, (await repo.GetSnapshotByAsOfDateAsync(Tenant, Member, new DateTime(2026, 6, 1)))!.PlanYearStart);
        Assert.Equal(new DateTime(2025, 1, 1), (await repo.GetSnapshotByAsOfDateAsync(Tenant, Member, new DateTime(2025, 6, 1)))!.PlanYearStart);
        Assert.Null(await repo.GetSnapshotByAsOfDateAsync(Tenant, Member, new DateTime(2024, 6, 1)));
        Assert.Equal(new[] { YearStart, new DateTime(2025, 1, 1) },
            (await repo.GetSnapshotsAsync(Tenant, Member)).Select(s => s.PlanYearStart));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
