using CloudHealthOffice.Testing.Mongo;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using MongoDB.Driver;

namespace EnrollmentImportService.Tests.Repositories;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EnrollmentExceptionRepositoryMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private EnrollmentExceptionRepository _repo = null!;
    private static readonly DateTime Now = new(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enrollment_exceptions");
        _repo = new EnrollmentExceptionRepository(_db);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static EnrollmentException Make(string id, string tenant = "t1", EnrollmentExceptionStatus status = EnrollmentExceptionStatus.Open,
        DateTime? heldUntil = null, int minutesAgo = 10) => new()
    {
        Id = id, TenantId = tenant, Number = EnrollmentException.NumberFor(id), Status = status, HeldUntil = heldUntil,
        ReasonCode = "SUBSCRIBER_NOT_FOUND", GroupNumber = "GRP1", MemberId = "M1",
        SourceKind = EnrollmentExceptionSourceKind.Dependent, Dependent = new Dependent { FirstName = "A", SubscriberId = "S1" },
        CreatedAt = Now.AddMinutes(-minutesAgo), UpdatedAt = Now.AddMinutes(-minutesAgo),
    };

    [Fact]
    public async Task Create_IsOncePerRow_AndReadsAreTenantScoped()
    {
        (await _repo.TryCreateAsync(Make("x1"))).Should().BeTrue();
        (await _repo.TryCreateAsync(Make("x1"))).Should().BeFalse("the same rejected row recorded twice");

        var stored = await _repo.GetAsync("t1", "x1");
        stored!.Dependent!.SubscriberId.Should().Be("S1");
        stored.Status.Should().Be(EnrollmentExceptionStatus.Open);
        (await _repo.GetAsync("t2", "x1")).Should().BeNull();
        (await _repo.ListAsync("t2", new EnrollmentExceptionQuery())).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_IsACompareAndSwapOnTheVersion()
    {
        await _repo.TryCreateAsync(Make("x1"));
        var a = (await _repo.GetAsync("t1", "x1"))!;
        var b = (await _repo.GetAsync("t1", "x1"))!;

        a.Status = EnrollmentExceptionStatus.Held;
        await _repo.SaveAsync(a, a.Version);
        b.Status = EnrollmentExceptionStatus.Resolved;
        var stale = () => _repo.SaveAsync(b, b.Version);

        await stale.Should().ThrowAsync<EnrollmentExceptionConcurrencyException>();
        (await _repo.GetAsync("t1", "x1"))!.Status.Should().Be(EnrollmentExceptionStatus.Held);
    }

    [Fact]
    public async Task Workable_IsOpenAndExpiredHolds_OldestFirst_Paged()
    {
        await _repo.TryCreateAsync(Make("open-new", minutesAgo: 1));
        await _repo.TryCreateAsync(Make("open-old", minutesAgo: 30));
        await _repo.TryCreateAsync(Make("held-expired", status: EnrollmentExceptionStatus.Held, heldUntil: Now.AddMinutes(-1), minutesAgo: 20));
        await _repo.TryCreateAsync(Make("held", status: EnrollmentExceptionStatus.Held, heldUntil: Now.AddDays(1)));
        await _repo.TryCreateAsync(Make("done", status: EnrollmentExceptionStatus.Resolved));
        await _repo.TryCreateAsync(Make("other-tenant", tenant: "t2"));

        var (first, total) = await _repo.ListWorkableAsync("t1", Now, 1, 2);
        var (second, _) = await _repo.ListWorkableAsync("t1", Now, 2, 2);

        total.Should().Be(3);
        first.Select(e => e.Id).Should().Equal("open-old", "held-expired");
        second.Select(e => e.Id).Should().Equal("open-new");
    }
}
