using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Migrations;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Migrations;

/// <summary>
/// The one-time Pay/Deny backfill against a real mongod: claims Denied at
/// adjudication with no payment-cycle evidence move to Deny (non-terminal);
/// finalized denials, remitted ids, and everything else stay put; dry-run
/// writes nothing; a rerun is a no-op.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class DenyStatusBackfillTests : IAsyncLifetime
{
    private const string Tenant = "tenant-backfill";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private ClaimRepositoryMongo _repo = null!;
    private DenyStatusBackfillService _sut = null!;

    public DenyStatusBackfillTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("deny_backfill_test");
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        _repo = new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ClaimRepositoryMongo>.Instance);
        _sut = new DenyStatusBackfillService(_repo, NullLogger<DenyStatusBackfillService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private async Task<Claim> SeedAsync(string id, ClaimStatus status, Action<Claim>? shape = null, bool legacyRow = false)
    {
        var claim = new Claim
        {
            Id = id,
            TenantId = Tenant,
            ClaimNumber = "CN-" + id,
            MemberId = "M1",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            Status = status,
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            SubmittedDate = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
            AdjudicationResult = new AdjudicationResult { PayerPayment = 0m, DenialReasonCode = "27" },
        };
        shape?.Invoke(claim);
        if (legacyRow)
            // Predates the version chain: no version fields at all.
            await _database.GetCollection<Claim>("Claims").InsertOneAsync(claim);
        else
            await _repo.CreateAsync(claim);
        return claim;
    }

    private async Task SeedFixtureAsync()
    {
        await SeedAsync("legacy-denied", ClaimStatus.Denied);
        await SeedAsync("legacy-denied-unversioned", ClaimStatus.Denied, legacyRow: true);
        await SeedAsync("remitted-in-835", ClaimStatus.Denied);
        await SeedAsync("finalized-denied", ClaimStatus.Denied, c =>
        {
            c.AdjudicationResult!.CheckNumber = "PR-0001-D1";
            c.AdjudicationResult.PaymentDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            c.EDI835ControlNumber = "PR-0001";
        });
        await SeedAsync("already-deny", ClaimStatus.Deny);
        await SeedAsync("paid", ClaimStatus.Paid);
    }

    private static DenyStatusBackfillRequest Request(bool dryRun) => new()
    {
        DryRun = dryRun,
        RemittedClaimIds = ["remitted-in-835"],
    };

    [Fact]
    public async Task DryRun_ListsTheCandidates_AndWritesNothing()
    {
        await SeedFixtureAsync();

        var result = await _sut.RunAsync(Tenant, Request(dryRun: true), "ops", default);

        result.DryRun.Should().BeTrue();
        result.Reopened.Should().BeEquivalentTo("legacy-denied", "legacy-denied-unversioned");
        result.SkippedRemitted.Should().Equal("remitted-in-835");
        result.Candidates.Should().Be(3);
        (await _repo.GetByIdAsync("legacy-denied"))!.Status.Should().Be(ClaimStatus.Denied);
        (await _repo.GetByIdAsync("legacy-denied-unversioned"))!.Status.Should().Be(ClaimStatus.Denied);
    }

    [Fact]
    public async Task Apply_MovesUnremittedDenials_ToNonTerminalDeny_AndIsIdempotent()
    {
        await SeedFixtureAsync();

        var applied = await _sut.RunAsync(Tenant, Request(dryRun: false), "ops", default);

        applied.Reopened.Should().BeEquivalentTo("legacy-denied", "legacy-denied-unversioned");
        foreach (var id in new[] { "legacy-denied", "legacy-denied-unversioned" })
        {
            var moved = (await _repo.GetByIdAsync(id))!;
            moved.Status.Should().Be(ClaimStatus.Deny);
            moved.VersionState.Should().Be(ClaimVersionState.Adjudicated);
            moved.AdjudicationResult!.DenialReasonCode.Should().Be("27", "only the status moves");
        }
        (await _repo.GetByIdAsync("remitted-in-835"))!.Status.Should().Be(ClaimStatus.Denied);
        (await _repo.GetByIdAsync("finalized-denied"))!.Status.Should().Be(ClaimStatus.Denied);
        (await _repo.GetByIdAsync("already-deny"))!.Status.Should().Be(ClaimStatus.Deny);
        (await _repo.GetByIdAsync("paid"))!.Status.Should().Be(ClaimStatus.Paid);

        // A moved claim is editable again (reprocess / examiner resolve).
        var reopened = (await _repo.GetByIdAsync("legacy-denied"))!;
        reopened.ClaimNotes = "reprocessed";
        await FluentActions.Awaiting(() => _repo.UpdateAsync(reopened)).Should().NotThrowAsync();

        var rerun = await _sut.RunAsync(Tenant, Request(dryRun: false), "ops", default);
        rerun.Reopened.Should().BeEmpty();
        rerun.SkippedRemitted.Should().Equal("remitted-in-835");
        rerun.SkippedChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Reopen_IsConditional_OnTheRowStillBeingAnUnremittedDenial()
    {
        await SeedAsync("raced", ClaimStatus.Denied);
        var candidates = await _repo.FindLegacyUnremittedDeniedAsync(10);
        candidates.Select(c => c.Id).Should().Equal("raced");

        // A remittance lands between the listing and the write.
        await _database.GetCollection<Claim>("Claims").UpdateOneAsync(
            Builders<Claim>.Filter.Eq(c => c.Id, "raced"),
            Builders<Claim>.Update.Set(c => c.AdjudicationResult!.CheckNumber, "PR-0002-D1"));

        (await _repo.TryReopenLegacyDeniedAsync(Tenant, "raced")).Should().BeFalse();
        (await _repo.GetByIdAsync("raced"))!.Status.Should().Be(ClaimStatus.Denied);
    }

    [Theory]
    [InlineData(ClaimStatus.Denied, null, true)]
    [InlineData(ClaimStatus.Denied, "PR-0001-D1", false)]
    [InlineData(ClaimStatus.Deny, null, false)]
    [InlineData(ClaimStatus.Approved, null, false)]
    public void IsLegacyUnremittedDenied_NeedsDeniedWithoutPaymentCycleEvidence(ClaimStatus status, string? check, bool expected)
    {
        var claim = new Claim
        {
            Status = status,
            VersionState = ClaimRepository.MapStatusToVersionState(status),
            AdjudicationResult = new AdjudicationResult { CheckNumber = check },
        };

        ClaimRepository.IsLegacyUnremittedDenied(claim).Should().Be(expected);
    }
}
