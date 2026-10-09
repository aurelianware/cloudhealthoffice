using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// PR #1278 round 3 (L10) and its verification (L6), on a real mongod: the
/// examiner-resolution lock is a conditional write on Pended + no live lock,
/// and the final write is fenced on the lock token — a resolver whose lock
/// expired and was taken over cannot finalize.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimRepositoryResolutionLockTests : IAsyncLifetime
{
    private const string Tenant = "tenant-lock";
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    // Built per use: HttpContextAccessor keeps the context in an AsyncLocal,
    // which does not flow from InitializeAsync into the test method.
    private ClaimRepositoryMongo _repo
    {
        get
        {
            var ctx = new DefaultHttpContext();
            ctx.Items["TenantId"] = Tenant;
            return new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx },
                NullLogger<ClaimRepositoryMongo>.Instance);
        }
    }

    public ClaimRepositoryResolutionLockTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_resolution_lock_test");
        await _database.GetCollection<Claim>("Claims").InsertOneAsync(new Claim
        {
            Id = "C1", ClaimVersionId = "C1", TenantId = Tenant, ClaimNumber = "CN-1", MemberId = "M1",
            BillingProviderNPI = "1234567890", Status = ClaimStatus.Pended, VersionState = ClaimVersionState.Submitted,
            ServiceDateFrom = Now, ServiceDateTo = Now,
        });
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task OneResolutionAtATime_UntilTheLockExpires()
    {
        (await _repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10))).Should().BeTrue();
        (await _repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(1), TimeSpan.FromMinutes(10))).Should().BeFalse();
        (await _repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10))).Should().BeTrue();
    }

    /// <summary>
    /// examiner-1's re-run outlived its lock; examiner-2 took it over. Its
    /// final write is refused (null), examiner-2's goes through.
    /// </summary>
    [Fact]
    public async Task FinalWrite_IsFencedOnTheLockToken()
    {
        await _repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        await _repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));
        var claim = (await _repo.GetByIdAsync("C1"))!;
        claim.Status = ClaimStatus.Approved;
        claim.ResolutionLock = null;

        (await _repo.UpdateHoldingResolutionLockAsync(claim, "t1")).Should().BeNull();
        (await _repo.GetByIdAsync("C1"))!.Status.Should().Be(ClaimStatus.Pended);

        (await _repo.UpdateHoldingResolutionLockAsync(claim, "t2")).Should().NotBeNull();
        var stored = (await _repo.GetByIdAsync("C1"))!;
        stored.Status.Should().Be(ClaimStatus.Approved);
        stored.ResolutionLock.Should().BeNull();
    }
}
