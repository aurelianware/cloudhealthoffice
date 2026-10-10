using System.Net;
using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// The accumulator service over the real Cosmos stores
/// (<see cref="AccumulatorRepositoryCosmos"/>, <see cref="ProcessedClaimStoreCosmos"/>)
/// on the Cosmos DB emulator — the Cosmos twin of
/// <see cref="AccumulatorMongoStoreTests"/>: processed-claim markers and
/// their ETag-conditional lease takeover / completion, version-conditional
/// snapshot writes, the one-row-per-(snapshot, version) event append, the
/// reversal-kind and tombstone rows and the queries that find them. The
/// client is configured as accumulator-service's Program.cs configures it
/// (SDK serializer, camelCase). The store hard-codes its database name, so
/// tests isolate on a unique tenant id instead of a unique database.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class AccumulatorCosmosStoreTests(CosmosEmulatorFixture cosmos) : IAsyncLifetime
{
    private const string Member = "m-400";
    private static readonly DateTime YearStart = new(2026, 1, 1);
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
    private CosmosClient _client = null!;

    public async Task InitializeAsync()
    {
        cosmos.SkipIfUnavailable();
        _client = cosmos.CreateClient(serializerOptions: new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        });
        var db = (await _client.CreateDatabaseIfNotExistsAsync("CloudHealthOffice")).Database;
        foreach (var name in new[] { "AccumulatorSnapshots", "AccumulatorEvents", "AccumulatorProcessedClaims" })
            await CosmosEmulatorFixture.CreateContainerAsync(db, name);

        await Repo().TryReplaceSnapshotAsync(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(_tenant, Member, YearStart),
            TenantId = _tenant,
            MemberId = Member,
            PlanYearStart = YearStart,
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = 2000m,
            IndividualOopLimit = 8000m,
        }, expectedVersion: 0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AccumulatorRepositoryCosmos Repo() => new(_client);

    private global::AccumulatorService.Services.AccumulatorService Service(IProcessedClaimStore? store = null) =>
        new(Repo(), store ?? new ProcessedClaimStoreCosmos(_client), new RecordingPublisher(),
            NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);

    private async Task<AccumulatorSnapshot> Snapshot() =>
        (await Repo().GetSnapshotAsync(_tenant, Member, YearStart))!;

    private ClaimFinalizedEvent Claim(
        string claimId, decimal deductible, string status = "Paid", string? frequency = null, string? original = null) => new()
    {
        TenantId = _tenant,
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

    // ── Service scenarios (mirror AccumulatorMongoStoreTests) ────────────

    [SkippableFact]
    public async Task ApplyThenVoid_CountsOnce_AndBacksItOut_WithAVoidReversalRow()
    {
        var sut = Service();

        Assert.Equal(ApplyOutcome.Applied, (await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m))).Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, (await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m))).Outcome);
        Assert.Equal(300m, (await Snapshot()).IndividualDeductibleUsed);

        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed"));
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed")); // replay

        Assert.Equal(0m, (await Snapshot()).IndividualDeductibleUsed);
        var reversal = await Repo().GetClaimReversedEventAsync(_tenant, "C1");
        Assert.NotNull(reversal);
        Assert.Equal(ReversalKinds.Void, reversal!.ReversalKind);
        Assert.Equal(-300m, reversal.DeductibleDelta);
        Assert.Single((await Repo().GetEventsAsync(_tenant, Member)).Where(e => e.EventType == "ClaimReversed"));
        var applied = await Repo().GetClaimAppliedEventAsync(_tenant, "C1");
        Assert.Equal(300m, applied!.DeductibleDelta);
    }

    [SkippableFact]
    public async Task ReplacementBeforeOriginal_CountsOnce()
    {
        var sut = Service();

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));
        var c1 = await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m));
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        Assert.Equal(300m, (await Snapshot()).IndividualDeductibleUsed);
        // The reversal that arrived before its original is held as a tombstone.
        var tombstone = await new ProcessedClaimStoreCosmos(_client).GetAsync(_tenant, "C1:reversal");
        Assert.NotNull(tombstone);
        Assert.Equal(ReversalKinds.Replacement, tombstone!.ReversalKind);
    }

    [SkippableFact]
    public async Task ReplacementWhileOriginalInFlight_ReleasesItsReversalMarker()
    {
        var store = new ProcessedClaimStoreCosmos(_client);
        await store.TryBeginAsync(_tenant, "C1");

        var waiting = await Service(store).ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.InProgress, waiting.Outcome);
        Assert.Null(await store.GetAsync(_tenant, "C1:reversal"));
        Assert.Equal(0m, (await Snapshot()).IndividualDeductibleUsed);
    }

    // ── Processed-claim markers: ETag leases ─────────────────────────────

    [SkippableFact]
    public async Task Lease_InFlightThenTakenOver()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreCosmos(_client, TimeSpan.FromMinutes(2), clock);

        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(_tenant, "C9"));
        Assert.Equal(BeginClaimOutcome.InProgress, await store.TryBeginAsync(_tenant, "C9"));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(_tenant, "C9"));
        // Only one retry takes an expired lease over.
        Assert.Equal(BeginClaimOutcome.InProgress, await store.TryBeginAsync(_tenant, "C9"));

        await store.CompleteAsync(_tenant, "C9", "e", "Applied");
        await store.ReleaseAsync(_tenant, "C9"); // terminal: left alone
        Assert.Equal(BeginClaimOutcome.AlreadyApplied, await store.TryBeginAsync(_tenant, "C9"));
    }

    /// <summary>
    /// Many consumers retry one expired marker at the same moment: the
    /// ETag-conditional replace lets exactly one of them take it over.
    /// </summary>
    [SkippableFact]
    public async Task ExpiredLease_ConcurrentTakeover_HasExactlyOneWinner()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreCosmos(_client, TimeSpan.FromMinutes(2), clock);
        await store.BeginLeaseAsync(_tenant, "C7");
        clock.Advance(TimeSpan.FromMinutes(3));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.BeginLeaseAsync(_tenant, "C7")));

        var winners = attempts.Where(a => a.Outcome == BeginClaimOutcome.Proceed).ToList();
        Assert.Single(winners);
        Assert.All(attempts.Except(winners), a => Assert.Equal(BeginClaimOutcome.InProgress, a.Outcome));
        Assert.Equal(winners[0].Token, (await store.GetAsync(_tenant, "C7"))!.LeaseToken);
    }

    /// <summary>
    /// An attempt whose lease was taken over cannot complete the marker: the
    /// new holder's outcome (here a reversal tombstone with its kind) stands.
    /// </summary>
    [SkippableFact]
    public async Task CompleteLease_IsConditionalOnTheLeaseToken_AndKeepsTheReversalKind()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreCosmos(_client, TimeSpan.FromMinutes(2), clock);
        var stale = await store.BeginLeaseAsync(_tenant, "C5:reversal");
        clock.Advance(TimeSpan.FromMinutes(3));
        var fresh = await store.BeginLeaseAsync(_tenant, "C5:reversal");
        Assert.Equal(BeginClaimOutcome.Proceed, fresh.Outcome);
        Assert.NotEqual(stale.Token, fresh.Token);

        Assert.False(await store.CompleteLeaseAsync(_tenant, "C5:reversal", stale.Token!, "ev-stale", "Reversed", ReversalKinds.Denial));
        Assert.True(await store.CompleteLeaseAsync(_tenant, "C5:reversal", fresh.Token!, "C6", "ReversedBeforeApply", ReversalKinds.Void));
        Assert.False(await store.CompleteLeaseAsync(_tenant, "C5:reversal", fresh.Token!, "ev-again", "Reversed"), "already terminal");
        Assert.False(await store.CompleteLeaseAsync(_tenant, "missing", fresh.Token!, "x", "Applied"));

        var marker = (await store.GetAsync(_tenant, "C5:reversal"))!;
        Assert.Equal("ReversedBeforeApply", marker.Outcome);
        Assert.Equal("C6", marker.ResultingEventId);
        Assert.Equal(ReversalKinds.Void, marker.ReversalKind);
        Assert.Equal(BeginClaimOutcome.AlreadyApplied, await store.TryBeginAsync(_tenant, "C5:reversal"));
    }

    [SkippableFact]
    public async Task Release_DeletesOnlyAPendingMarker()
    {
        var store = new ProcessedClaimStoreCosmos(_client);
        await store.BeginLeaseAsync(_tenant, "C3");
        await store.ReleaseAsync(_tenant, "C3");
        Assert.Null(await store.GetAsync(_tenant, "C3"));
        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(_tenant, "C3"));

        await store.ReleaseAsync(_tenant, "never-begun"); // no throw
    }

    /// <summary>
    /// Release is ETag-pinned: a completion that lands between its read and
    /// its delete keeps the marker (the delete gets 412 and gives up).
    /// </summary>
    [SkippableFact]
    public async Task Release_LosesToACompletionThatRacesIt()
    {
        var store = new ProcessedClaimStoreCosmos(_client);
        var lease = await store.BeginLeaseAsync(_tenant, "C4");
        var container = _client.GetContainer("CloudHealthOffice", "AccumulatorProcessedClaims");
        var id = ProcessedClaim.BuildId(_tenant, "C4");
        var read = await container.ReadItemAsync<ProcessedClaim>(id, new PartitionKey(_tenant));

        Assert.True(await store.CompleteLeaseAsync(_tenant, "C4", lease.Token!, "ev-1", "Applied"));
        var ex = await Assert.ThrowsAsync<CosmosException>(() => container.DeleteItemAsync<ProcessedClaim>(
            id, new PartitionKey(_tenant), new ItemRequestOptions { IfMatchEtag = read.ETag }));
        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);

        await store.ReleaseAsync(_tenant, "C4");
        Assert.Equal("Applied", (await store.GetAsync(_tenant, "C4"))!.Outcome);
    }

    // ── Snapshot and event rows ──────────────────────────────────────────

    [SkippableFact]
    public async Task SnapshotWrite_IsVersionConditional()
    {
        var repo = Repo();
        var snap = await Snapshot();
        snap.Version = 1;
        snap.IndividualDeductibleUsed = 10m;

        Assert.True(await repo.TryReplaceSnapshotAsync(snap, expectedVersion: 0));
        Assert.False(await repo.TryReplaceSnapshotAsync(snap, expectedVersion: 0));
        Assert.Equal(10m, (await Snapshot()).IndividualDeductibleUsed);

        // A second creator of the same snapshot conflicts on the id.
        var duplicate = await Snapshot();
        duplicate.Id = AccumulatorSnapshot.BuildId(_tenant, "m-new", YearStart);
        duplicate.MemberId = "m-new";
        Assert.True(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 0));
        Assert.False(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 0));
        Assert.False(await repo.TryReplaceSnapshotAsync(duplicate, expectedVersion: 7), "no such version");
    }

    /// <summary>
    /// Two writers both read version N and both try to write N+1: the ETag
    /// on the replace lets exactly one through.
    /// </summary>
    [SkippableFact]
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

    [SkippableFact]
    public async Task EventAppend_IsOneRowPerSnapshotVersion()
    {
        var repo = Repo();
        var aggregate = AccumulatorSnapshot.BuildId(_tenant, Member, YearStart);
        AccumulatorEvent Row(long version, string type = "ClaimApplied", string? claim = "C1") => new()
        {
            Id = AccumulatorEvent.BuildId(aggregate, version), TenantId = _tenant, AggregateId = aggregate,
            Version = version, MemberId = Member, PlanYearStart = YearStart, EventType = type,
            SourceClaimId = claim, SourceReference = claim, DeductibleDelta = 10m * version,
            OccurredAt = new DateTime(2026, 3, 1).AddMinutes(version),
        };

        Assert.True(await repo.TryAppendEventAsync(Row(1)));
        Assert.False(await repo.TryAppendEventAsync(Row(1, claim: "C-other")), "a second writer at v1 conflicts");
        Assert.True(await repo.TryAppendEventAsync(Row(2, "ClaimReversed")));
        Assert.True(await repo.TryAppendEventAsync(Row(3, global::AccumulatorService.Services.AccumulatorService.ClaimTombstonedEventType, "C2")));
        Assert.True(await repo.TryAppendEventAsync(Row(4, "ManualAdjustment", "ADJ-1")));

        Assert.Equal(new long[] { 2, 3, 4 }, (await repo.GetAggregateEventsAsync(_tenant, aggregate, afterVersion: 1)).Select(e => e.Version));
        Assert.Equal(new long[] { 4, 3 }, (await repo.GetEventsAsync(_tenant, Member, take: 2)).Select(e => e.Version));
        Assert.Equal(2, (await repo.GetClaimReversedEventAsync(_tenant, "C1"))!.Version);
        Assert.Null(await repo.GetClaimReversedEventAsync(_tenant, "C2"));
        Assert.Equal(1, (await repo.GetClaimAppliedEventAsync(_tenant, "C1"))!.Version);
        Assert.Equal(4, (await repo.GetManualAdjustmentAsync(_tenant, "ADJ-1"))!.Version);
        Assert.Empty(await repo.GetAggregateEventsAsync("other-tenant", aggregate));
    }

    [SkippableFact]
    public async Task SnapshotQueries_FindThePlanYear()
    {
        var repo = Repo();
        var prior = await Snapshot();
        prior.Id = AccumulatorSnapshot.BuildId(_tenant, Member, new DateTime(2025, 1, 1));
        prior.PlanYearStart = new DateTime(2025, 1, 1);
        prior.PlanYearEnd = new DateTime(2025, 12, 31);
        Assert.True(await repo.TryReplaceSnapshotAsync(prior, expectedVersion: 0));

        Assert.Equal(YearStart, (await repo.GetSnapshotByAsOfDateAsync(_tenant, Member, new DateTime(2026, 6, 1)))!.PlanYearStart);
        Assert.Equal(new DateTime(2025, 1, 1), (await repo.GetSnapshotByAsOfDateAsync(_tenant, Member, new DateTime(2025, 6, 1)))!.PlanYearStart);
        Assert.Null(await repo.GetSnapshotByAsOfDateAsync(_tenant, Member, new DateTime(2024, 6, 1)));
        Assert.Equal(new[] { YearStart, new DateTime(2025, 1, 1) },
            (await repo.GetSnapshotsAsync(_tenant, Member)).Select(s => s.PlanYearStart));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
