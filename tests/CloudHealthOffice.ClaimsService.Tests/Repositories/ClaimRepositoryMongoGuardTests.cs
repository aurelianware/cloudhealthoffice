using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// The conditional writes of <see cref="ClaimRepositoryMongo"/> on a real
/// mongod: the resolution-lock fence, the final-disposition and synchronous
/// write-back guards, the void/supersede projections and tenant isolation.
/// Ported from the Cosmos emulator suite when the Cosmos claim repository was
/// removed (Phase 5b), for the behaviours <see cref="ClaimRepositoryResolutionLockTests"/>
/// and <see cref="ClaimRepositoryVersioningTests"/> did not already cover.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimRepositoryMongoGuardTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-guard";
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_guards");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    // Built per use: HttpContextAccessor keeps the context in an AsyncLocal.
    private ClaimRepositoryMongo Repo
    {
        get
        {
            var ctx = new DefaultHttpContext();
            ctx.Items["TenantId"] = Tenant;
            return new ClaimRepositoryMongo(_db, new HttpContextAccessor { HttpContext = ctx },
                NullLogger<ClaimRepositoryMongo>.Instance);
        }
    }

    private Task<Claim> SeedAsync(
        string id = "C1",
        ClaimStatus status = ClaimStatus.Pended,
        AdjudicationResult? adjudication = null) =>
        Repo.CreateAsync(new Claim
        {
            Id = id, ClaimVersionId = id, TenantId = Tenant, ClaimNumber = "CN-" + id, MemberId = "M1",
            BillingProviderNPI = "1234567890", Status = status,
            VersionState = ClaimRepository.MapStatusToVersionState(status),
            ServiceDateFrom = Now, ServiceDateTo = Now,
            AdjudicationResult = adjudication,
        });

    private async Task<Claim> StoredAsync(string id = "C1") => (await Repo.GetByIdAsync(id))!;

    private Task<bool> AcquireAsync(string token, DateTime at) =>
        Repo.TryAcquireResolutionLockAsync(Tenant, "C1", token, "examiner-" + token, at, TimeSpan.FromMinutes(10));

    // ── Resolution lock ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolutionLock_IsOnlyTakenOnAReprocessableClaim()
    {
        // Pended, Pay (Approved) and Deny stay workable until the payment cycle;
        // a finalized claim (Paid / Denied) is never locked for resolution.
        await SeedAsync(status: ClaimStatus.Paid);

        (await AcquireAsync("t1", Now)).Should().BeFalse();
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "missing", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10)))
            .Should().BeFalse();
        (await StoredAsync()).ResolutionLock.Should().BeNull();
    }

    [Fact]
    public async Task FinalWrite_LosesToAConcurrentWrite_BetweenItsReadAndItsReplace()
    {
        await SeedAsync();
        await AcquireAsync("t1", Now);
        var claim = await StoredAsync();
        claim.Status = ClaimStatus.Approved;

        // Another writer lands after the resolver's read, still under its lock.
        await _db.GetCollection<BsonDocument>("Claims").UpdateOneAsync(
            new BsonDocument("_id", "C1"),
            new BsonDocument
            {
                { "$set", new BsonDocument("LastUpdatedBy", "someone-else") },
                { "$inc", new BsonDocument("Revision", 1) },
            });

        var act = () => Repo.UpdateHoldingResolutionLockAsync(claim, "t1");

        await act.Should().ThrowAsync<ClaimConcurrencyException>();
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.LastUpdatedBy.Should().Be("someone-else");
    }

    /// <summary>
    /// The fenced re-run under the live token writes the projection, but the
    /// claim is Pended, which the synchronous write-back guard protects; the
    /// status stays Pended for the examiner's own final write.
    /// </summary>
    [Fact]
    public async Task FencedRerun_UnderTheLiveToken_WritesTheProjection_ButNotOverAPend()
    {
        await SeedAsync();
        await AcquireAsync("t1", Now);

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = 58m, AllowedAmount = 58m }, [],
            resolvedStatus: ClaimStatus.Approved, requiredResolutionLockToken: "t1")).Should().BeTrue();

        var stored = await StoredAsync();
        stored.AdjudicationResult!.PayerPayment.Should().Be(58m);
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.ResolutionLock!.Token.Should().Be("t1");
    }

    [Fact]
    public async Task ReleaseResolutionLock_OnlyReleasesTheHoldersToken()
    {
        await SeedAsync();
        await AcquireAsync("t1", Now);

        await Repo.ReleaseResolutionLockAsync(Tenant, "C1", "someone-else");
        (await StoredAsync()).ResolutionLock!.Token.Should().Be("t1");

        await Repo.ReleaseResolutionLockAsync(Tenant, "C1", "t1");
        (await StoredAsync()).ResolutionLock.Should().BeNull();
        await Repo.ReleaseResolutionLockAsync(Tenant, "missing", "t1"); // gone: no throw

        // Released, so a new resolver can take it before the old expiry.
        (await AcquireAsync("t2", Now.AddMinutes(1))).Should().BeTrue();
    }

    [Fact]
    public async Task FencedPendProjection_WithATakenOverToken_WritesNothing()
    {
        await SeedAsync();
        await AcquireAsync("t1", Now);
        await AcquireAsync("t2", Now.AddMinutes(11));

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { AllowedAmount = 99m }, [], isPend: true,
            requiredResolutionLockToken: "t1")).Should().BeFalse();

        (await StoredAsync()).AdjudicationResult.Should().BeNull();
    }

    // ── Final-disposition guard on the Pend projection ───────────────────

    [Theory]
    [InlineData(ClaimStatus.Paid)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Voided)]
    public async Task Projection_OfATerminalVersion_IsRefused(ClaimStatus terminal)
    {
        await SeedAsync(status: terminal);

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { AllowedAmount = 1m }, [], isPend: true)).Should().BeFalse();

        var stored = await StoredAsync();
        stored.Status.Should().Be(terminal);
        stored.AdjudicationResult.Should().BeNull();
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted)]
    [InlineData(ClaimStatus.Pended)]
    public async Task PendProjection_PendsAClaimWithNoFinalDisposition(ClaimStatus status)
    {
        await SeedAsync(status: status);

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult(), [], isPend: true)).Should().BeTrue();

        (await StoredAsync()).Status.Should().Be(ClaimStatus.Pended);
    }

    // ── Guarded status write (TryTransitionStatusAsync) ─────────────────

    [Fact]
    public async Task TransitionStatus_OnAnOpenClaim_IsApplied_WithItsVersionState()
    {
        await SeedAsync(status: ClaimStatus.Submitted);

        var result = await Repo.TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Denied);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Applied, ClaimStatus.Denied));
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Denied);
        stored.VersionState.Should().Be(ClaimRepository.MapStatusToVersionState(ClaimStatus.Denied));
    }

    [Theory]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Paid)]
    [InlineData(ClaimStatus.Voided)]
    public async Task TransitionStatus_OverAPendOrFinalDisposition_IsSuppressed(ClaimStatus guarded)
    {
        await SeedAsync(status: guarded);
        var desired = guarded == ClaimStatus.Approved ? ClaimStatus.Denied : ClaimStatus.Approved;

        var result = await Repo.TryTransitionStatusAsync(Tenant, "C1", desired);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Suppressed, guarded));
        (await StoredAsync()).Status.Should().Be(guarded);
    }

    // ── Void / supersede projections ─────────────────────────────────────

    [Fact]
    public async Task VoidAndSupersede_Projections_PatchTheRow()
    {
        await SeedAsync(id: "C1", status: ClaimStatus.Approved);
        await SeedAsync(id: "C2", status: ClaimStatus.Paid);

        (await Repo.MarkVoidedProjectionAsync(Tenant, "C1", Now, "actor-1")).Should().BeTrue();
        (await Repo.MarkSupersededProjectionAsync(Tenant, "C2", "C3", Now, "actor-1")).Should().BeTrue();
        (await Repo.MarkVoidedProjectionAsync(Tenant, "missing", Now, "actor-1")).Should().BeFalse();

        var voided = await StoredAsync("C1");
        voided.Status.Should().Be(ClaimStatus.Voided);
        voided.VersionState.Should().Be(ClaimVersionState.Voided);
        var superseded = await StoredAsync("C2");
        superseded.VersionState.Should().Be(ClaimVersionState.Adjusted);
        superseded.SupersededByVersionId.Should().Be("C3");

        // Voided is a final disposition: a late status write cannot reopen it.
        (await Repo.TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Pended)).Outcome
            .Should().Be(StatusWriteOutcome.Suppressed);
    }

    // ── Tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTenantsWrites_NeverReachTheRow()
    {
        await SeedAsync(status: ClaimStatus.Submitted);

        (await Repo.TryTransitionStatusAsync("other-tenant", "C1", ClaimStatus.Approved))
            .Should().Be(StatusWriteResult.NotFoundResult);
        (await Repo.TryAcquireResolutionLockAsync("other-tenant", "C1", "t1", "x", Now, TimeSpan.FromMinutes(1)))
            .Should().BeFalse();
        (await Repo.UpdateAdjudicationProjectionAsync("other-tenant", "C1", new AdjudicationResult(), [], isPend: true))
            .Should().BeFalse();
        (await Repo.MarkVoidedProjectionAsync("other-tenant", "C1", Now, "actor-1")).Should().BeFalse();
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Submitted);
    }
}
