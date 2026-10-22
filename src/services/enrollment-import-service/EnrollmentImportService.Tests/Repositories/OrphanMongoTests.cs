using CloudHealthOffice.Testing.Mongo;
using EnrollmentImportService.HostedServices;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Orphans;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EnrollmentImportService.Tests.Repositories;

/// <summary>The orphan queries, the settings store and the reprocess claim on a real MongoDB.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class OrphanMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private EnrollmentExceptionRepository _repo = null!;
    private static readonly DateTime Now = new(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enrollment_orphans");
        _repo = new EnrollmentExceptionRepository(_db);
        await new EnrollmentIndexInitializer(_db, NullLogger<EnrollmentIndexInitializer>.Instance).StartAsync(default);
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static EnrollmentException Orphan(string id, string tenant = "t1", string subscriberId = "S1",
        EnrollmentExceptionStatus status = EnrollmentExceptionStatus.Held, DateTime? heldUntil = null, bool autoHeld = true,
        string reason = EnrollmentRejectionReasons.SubscriberNotFound) => new()
    {
        Id = id, TenantId = tenant, Number = EnrollmentException.NumberFor(id), Status = status,
        HeldUntil = status == EnrollmentExceptionStatus.Held ? heldUntil ?? Now.AddDays(5) : null, AutoHeld = autoHeld,
        ReasonCode = reason, BatchId = "B1",
        SourceKind = EnrollmentExceptionSourceKind.Dependent,
        Dependent = new Dependent { FirstName = "A", SubscriberId = subscriberId, MaintenanceType = "021" },
        CreatedAt = Now, UpdatedAt = Now,
    };

    [Fact]
    public async Task OrphansForASubscriber_AreUnresolvedDependentsNamingIt_InOneTenant()
    {
        await _repo.TryCreateAsync(Orphan("held"));
        await _repo.TryCreateAsync(Orphan("open", status: EnrollmentExceptionStatus.Open));
        await _repo.TryCreateAsync(Orphan("resolved", status: EnrollmentExceptionStatus.Resolved));
        await _repo.TryCreateAsync(Orphan("other-subscriber", subscriberId: "S2"));
        await _repo.TryCreateAsync(Orphan("other-reason", reason: EnrollmentRejectionReasons.ValidationFailed));
        await _repo.TryCreateAsync(Orphan("other-tenant", tenant: "t2"));

        (await _repo.ListOrphansForSubscriberAsync("t1", "S1")).Select(e => e.Id).Should().BeEquivalentTo(["held", "open"]);
        (await _repo.ListOrphansForSubscriberAsync("t2", "S1")).Select(e => e.Id).Should().Equal("other-tenant");
    }

    [Fact]
    public async Task TheOrphanQueries_HaveTheirIndexes()
    {
        var indexes = await (await _db.GetCollection<BsonDocument>(EnrollmentExceptionRepository.CollectionName).Indexes.ListAsync()).ToListAsync();
        indexes.Select(i => i["name"].AsString).Should().Contain(["ix_tenant_reason_dependent_subscriber", "ix_autoheld_status_helduntil_tenant"]);
        var orphanIndex = indexes.Single(i => i["name"] == "ix_tenant_reason_dependent_subscriber")["key"].AsBsonDocument;
        orphanIndex.Names.Should().Equal("TenantId", "ReasonCode", "Dependent.SubscriberId");
    }

    [Fact]
    public async Task ExpiredAutomaticHolds_ByTenant_ExcludingSpecialistHoldsAndLiveOnes()
    {
        await _repo.TryCreateAsync(Orphan("ended", heldUntil: Now.AddMinutes(-1)));
        await _repo.TryCreateAsync(Orphan("live", heldUntil: Now.AddDays(1)));
        await _repo.TryCreateAsync(Orphan("manual", heldUntil: Now.AddMinutes(-1), autoHeld: false));
        await _repo.TryCreateAsync(Orphan("t2-ended", tenant: "t2", heldUntil: Now.AddDays(-2)));

        (await _repo.ListTenantsWithExpiredAutoHeldAsync(Now)).Should().BeEquivalentTo(["t1", "t2"]);
        (await _repo.ListExpiredAutoHeldAsync("t1", Now, 10)).Select(e => e.Id).Should().Equal("ended");
    }

    [Fact]
    public async Task TheReprocessClaim_IsReleasedAfterAMongoRoundTrip()
    {
        // The claim is released only when the stored value matches exactly;
        // MongoDB keeps milliseconds, so the claim must be whole milliseconds.
        await _repo.TryCreateAsync(Orphan("x1"));
        var import = new Mock<IEnrollmentImportService>();
        import.Setup(i => i.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>())).ReturnsAsync(new ImportResult());
        var clock = new OddTicksClock(Now.AddTicks(1234567));
        var service = new EnrollmentExceptionService(_repo, import.Object, clock);

        var result = await service.AutoRetryOrphanAsync("t1", "x1", "test");

        result.Outcome.Should().Be(ExceptionActionOutcome.Done);
        var stored = await _repo.GetAsync("t1", "x1");
        stored!.ReprocessLeaseUntil.Should().BeNull();
        stored.Status.Should().Be(EnrollmentExceptionStatus.Held);
        stored.Attempts.Should().Be(1);
    }

    private sealed class OddTicksClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
