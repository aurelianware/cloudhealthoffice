using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// The accumulator service over the real Mongo stores (EphemeralMongo):
/// PR #1278 round 3 — the replacement-before-original tombstone (B3), the
/// lease / release behaviour of <see cref="ProcessedClaimStoreMongo"/>,
/// version-conditional snapshot writes, and the startup index initializer
/// that de-duplicates reversal rows before creating the unique index (M7).
/// (Cosmos has no local emulator in CI; its store mirrors these paths with
/// ETag-conditional writes.)
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorMongoStoreTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Member = "m-400";
    private static readonly DateTime YearStart = new(2026, 1, 1);
    private IMongoDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("accumulators");
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        await new AccumulatorRepositoryMongo(_db).TryReplaceSnapshotAsync(new AccumulatorSnapshot
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

    private global::AccumulatorService.Services.AccumulatorService Service(ProcessedClaimStoreMongo? store = null) =>
        new(new AccumulatorRepositoryMongo(_db), store ?? new ProcessedClaimStoreMongo(_db), new RecordingPublisher(),
            NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);

    private async Task<AccumulatorSnapshot> Snapshot() =>
        (await new AccumulatorRepositoryMongo(_db).GetSnapshotAsync(Tenant, Member, YearStart))!;

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

    [Fact]
    public async Task ReplacementBeforeOriginal_CountsOnce()
    {
        var sut = Service();

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));
        var c1 = await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m));
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        Assert.Equal(300m, (await Snapshot()).IndividualDeductibleUsed);
    }

    [Fact]
    public async Task ReplacementWhileOriginalInFlight_ReleasesItsReversalMarker()
    {
        var store = new ProcessedClaimStoreMongo(_db);
        await store.TryBeginAsync(Tenant, "C1");

        var waiting = await Service(store).ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.InProgress, waiting.Outcome);
        Assert.Null(await store.GetAsync(Tenant, "C1:reversal"));
        Assert.Equal(0m, (await Snapshot()).IndividualDeductibleUsed);
    }

    [Fact]
    public async Task Lease_InFlightThenTakenOver()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new ProcessedClaimStoreMongo(_db, TimeSpan.FromMinutes(2), clock);

        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(Tenant, "C9"));
        Assert.Equal(BeginClaimOutcome.InProgress, await store.TryBeginAsync(Tenant, "C9"));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(BeginClaimOutcome.Proceed, await store.TryBeginAsync(Tenant, "C9"));
        // Only one retry takes an expired lease over.
        Assert.Equal(BeginClaimOutcome.InProgress, await store.TryBeginAsync(Tenant, "C9"));

        await store.CompleteAsync(Tenant, "C9", "e", "Applied");
        await store.ReleaseAsync(Tenant, "C9"); // terminal: left alone
        Assert.Equal(BeginClaimOutcome.AlreadyApplied, await store.TryBeginAsync(Tenant, "C9"));
    }

    [Fact]
    public async Task SnapshotWrite_IsVersionConditional()
    {
        var repo = new AccumulatorRepositoryMongo(_db);
        var snap = await Snapshot();
        snap.Version = 1;
        snap.IndividualDeductibleUsed = 10m;

        Assert.True(await repo.TryReplaceSnapshotAsync(snap, expectedVersion: 0));
        Assert.False(await repo.TryReplaceSnapshotAsync(snap, expectedVersion: 0));
        Assert.Equal(10m, (await Snapshot()).IndividualDeductibleUsed);
    }

    /// <summary>
    /// A database that already holds a double reversal (written before the
    /// unique index): creating the index over it would fail on every start
    /// and the consumer would loop. The initializer re-tags the extra row
    /// first, then the index holds.
    /// </summary>
    [Fact]
    public async Task IndexInitializer_RetagsExistingDuplicateReversals()
    {
        var db = mongo.CreateDatabase("accumulators_dupes");
        try
        {
            var events = db.GetCollection<AccumulatorEvent>(AccumulatorMongoIndexes.EventsCollection);
            AccumulatorEvent Reversal(long version) => new()
            {
                TenantId = Tenant, AggregateId = "agg", Version = version, MemberId = Member,
                EventType = "ClaimReversed", SourceClaimId = "C1", DeductibleDelta = -100m,
            };
            await events.InsertManyAsync([Reversal(2), Reversal(3)]);

            await AccumulatorMongoIndexes.EnsureAsync(db, NullLogger.Instance);
            await AccumulatorMongoIndexes.EnsureAsync(db, NullLogger.Instance); // idempotent

            var rows = await events.Find(FilterDefinition<AccumulatorEvent>.Empty).SortBy(e => e.Version).ToListAsync();
            Assert.Equal(new[] { "ClaimReversed", AccumulatorMongoIndexes.DuplicateReversalEventType },
                rows.Select(r => r.EventType));
            var repo = new AccumulatorRepositoryMongo(db);
            Assert.False(await repo.TryAppendEventAsync(Reversal(4)));
        }
        finally
        {
            await mongo.DropDatabaseAsync(db);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
