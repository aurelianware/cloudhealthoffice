using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// The accumulator outbox on the claim document and the accumulator-totals
/// rebuild source, on a real mongod (EphemeralMongo):
/// the commit lands with the status write that finalizes the claim or not at
/// all; the outbox entry is rescheduled / cleared only while it is still the
/// one that was driven; superseded versions do not count, and every counted
/// claim's contribution is returned.
/// </summary>
public abstract class ClaimRepositoryAccumulatorOutboxTests
{
    protected const string Tenant = "tenant-outbox";
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
    private const string PlanId = "8b9f1a2e-1111-4c4c-9a9a-000000000001";

    protected abstract IClaimRepository Repo { get; }

    private async Task<Claim> SeedAsync(string id, ClaimStatus status = ClaimStatus.Submitted,
        ClaimVersionState? versionState = null, decimal deductible = 0m, decimal oop = 0m, DateTime? supersededAt = null) =>
        await Repo.CreateAsync(new Claim
        {
            Id = id, ClaimVersionId = id, TenantId = Tenant, ClaimNumber = "CN-" + id,
            MemberId = "M1", SubscriberId = "S1", BenefitPlanId = PlanId,
            BillingProviderNPI = "1234567890", Status = status,
            VersionState = versionState ?? ClaimRepository.MapStatusToVersionState(status),
            ServiceDateFrom = Now, ServiceDateTo = Now,
            SupersededAt = supersededAt,
            AdjudicationResult = deductible + oop > 0
                ? new AdjudicationResult { NetworkTier = "InNetwork", DeductibleAmount = deductible, PatientResponsibility = oop, OopAppliedAmount = oop }
                : null,
        });

    private static AccumulatorOutboxItem Item(string claimId, DateTime? at = null) => AccumulatorOutboxItem.ForCommit(new AccumulatorCommit
    {
        CommitId = "commit-" + claimId, ClaimId = claimId, MemberId = "M1", SubscriberId = "S1",
        BenefitPlanId = Guid.Parse(PlanId), PlanYear = "2026",
        Updates =
        [
            new CloudHealthOffice.BenefitEngine.Services.AccumulatorUpdate { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Amount = 100m, Source = "Deductible", ClampAtLimit = 500m },
        ],
    }, at ?? Now);

    private Task<bool> FinalizeAsync(string id, ClaimStatus status, AccumulatorOutboxItem item, string? lockToken = null) =>
        Repo.UpdateAdjudicationProjectionAsync(
            Tenant, id, new AdjudicationResult { AllowedAmount = 100m, PayerPayment = 100m }, [],
            resolvedStatus: status, requiredResolutionLockToken: lockToken, pendingAccumulatorCommit: item);

    private async Task<Claim> StoredAsync(string id) => (await Repo.GetForAccumulatorOutboxAsync(Tenant, id))!;

    // ── the commit lands with the finalizing status write ───────────────

    [Fact]
    public async Task TheApprovedStatusWrite_CarriesTheCommit()
    {
        await SeedAsync("C1");
        var item = Item("C1");

        (await FinalizeAsync("C1", ClaimStatus.Approved, item)).Should().BeTrue();

        var stored = await StoredAsync("C1");
        stored.Status.Should().Be(ClaimStatus.Approved);
        stored.PendingAccumulatorCommit.Should().NotBeNull();
        stored.PendingAccumulatorCommit!.Id.Should().Be(item.Id);
        stored.PendingAccumulatorCommit.Commit!.CommitId.Should().Be("commit-C1");
        stored.PendingAccumulatorCommit.Commit.BenefitPlanId.Should().Be(Guid.Parse(PlanId));
        stored.PendingAccumulatorCommit.Commit.Updates.Should().ContainSingle(u =>
            u.Type == AccumulatorType.IndividualDeductible && u.Amount == 100m && u.ClampAtLimit == 500m);
        stored.PendingAccumulatorCommit.DueAtMs.Should().Be(AccumulatorOutboxItem.ToMs(Now + AccumulatorOutboxItem.InlineGrace));
    }

    [Fact]
    public async Task APendProjection_SavesNoCommit()
    {
        await SeedAsync("C1");

        (await Repo.UpdateAdjudicationProjectionAsync(Tenant, "C1", new AdjudicationResult(), [],
            pendDetails: new PendDetails { PendCode = "NCCI" }, isPend: true, pendingAccumulatorCommit: Item("C1"))).Should().BeTrue();

        (await StoredAsync("C1")).PendingAccumulatorCommit.Should().BeNull();
    }

    /// <summary>
    /// The status write is refused — the claim was finalized another way
    /// meanwhile, or it is Pended (an examiner's approval re-run: the
    /// examiner's lock-fenced final write carries the commit instead) — and
    /// the commit with it.
    /// </summary>
    [Theory]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Voided)]
    public async Task ABlockedStatusWrite_SavesNoCommit(ClaimStatus finalStatus)
    {
        await SeedAsync("C1", finalStatus);

        await FinalizeAsync("C1", ClaimStatus.Approved, Item("C1"));

        var stored = await StoredAsync("C1");
        stored.Status.Should().Be(finalStatus);
        stored.PendingAccumulatorCommit.Should().BeNull();
    }

    // ── due scan, reschedule, complete ──────────────────────────────────

    [Fact]
    public async Task TheEntry_IsDueAtItsTime_RescheduledAndClearedOnlyWhileItIsTheSame()
    {
        await SeedAsync("C1");
        var item = Item("C1");
        await FinalizeAsync("C1", ClaimStatus.Approved, item);
        var nowMs = AccumulatorOutboxItem.ToMs(Now + AccumulatorOutboxItem.InlineGrace); // a new entry is left to its writer for the inline grace

        (await Repo.FindDueAccumulatorOutboxAsync(nowMs - 1, 10)).Should().NotContain(c => c.Id == "C1");
        (await Repo.FindDueAccumulatorOutboxAsync(nowMs, 10)).Should().Contain(c => c.Id == "C1" && c.TenantId == Tenant);
        (await Repo.OldestAccumulatorOutboxAsync()).Should().BeLessThanOrEqualTo(nowMs);

        // Another entry's id: nothing changes.
        (await Repo.RescheduleAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, "other", 1, Now.AddMinutes(1), "x")).Should().BeFalse();
        (await Repo.CompleteAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, "other")).Should().BeFalse();
        (await Repo.CompleteAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Reversal, item.Id)).Should().BeFalse();

        (await Repo.RescheduleAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, item.Id, 1, Now.AddSeconds(30), "HttpRequestException 503")).Should().BeTrue();
        var rescheduled = (await StoredAsync("C1")).PendingAccumulatorCommit!;
        rescheduled.Attempts.Should().Be(1);
        rescheduled.LastError.Should().Be("HttpRequestException 503");
        rescheduled.DueAtMs.Should().Be(AccumulatorOutboxItem.ToMs(Now.AddSeconds(30)));
        (await Repo.FindDueAccumulatorOutboxAsync(AccumulatorOutboxItem.ToMs(Now.AddSeconds(29)), 10)).Should().NotContain(c => c.Id == "C1");
        (await Repo.FindDueAccumulatorOutboxAsync(AccumulatorOutboxItem.ToMs(Now.AddSeconds(30)), 10)).Should().Contain(c => c.Id == "C1");

        var review = new AccumulatorClampReview
        {
            CommitId = "commit-C1", RaisedAt = Now,
            Clamps = [new AccumulatorClamp { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Requested = 100m, Applied = 50m, Limit = 500m }],
        };
        (await Repo.CompleteAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, item.Id, review)).Should().BeTrue();

        var done = await StoredAsync("C1");
        done.PendingAccumulatorCommit.Should().BeNull();
        done.AccumulatorClampReview!.Clamps.Should().ContainSingle(c => c.Requested == 100m && c.Applied == 50m);
        (await Repo.FindDueAccumulatorOutboxAsync(long.MaxValue / 2, 10)).Should().NotContain(c => c.Id == "C1");
        (await Repo.CompleteAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, item.Id)).Should().BeFalse();

        (await Repo.GetAccumulatorClampReviewsAsync(10)).Should().ContainSingle(c => c.Id == "C1");
        (await Repo.ResolveAccumulatorClampReviewAsync("C1", "examiner-1")).Should().BeTrue();
        (await Repo.GetAccumulatorClampReviewsAsync(10)).Should().BeEmpty();
        (await StoredAsync("C1")).AccumulatorClampReview!.ResolvedBy.Should().Be("examiner-1");
    }

    /// <summary>
    /// The review is its own write keyed on the commit id: saved after the slot was cleared by
    /// another attempt, a no-op for the same commit, replaced by a later commit's review.
    /// </summary>
    [Fact]
    public async Task TheReview_IsKeyedOnTheCommit_NotOnTheSlot()
    {
        await SeedAsync("C1");
        var item = Item("C1");
        await FinalizeAsync("C1", ClaimStatus.Approved, item);
        (await Repo.CompleteAccumulatorOutboxAsync(Tenant, "C1", AccumulatorOutboxKind.Commit, item.Id)).Should().BeTrue();

        AccumulatorClampReview Review(string commit, decimal applied) => new()
        {
            CommitId = commit, Kind = AccumulatorClampReview.Clamped, RaisedAt = Now,
            Clamps = [new AccumulatorClamp { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Requested = 100m, Applied = applied, Limit = 500m }],
        };
        (await Repo.RecordAccumulatorReviewAsync(Tenant, "C1", Review("commit-C1", 50m))).Should().BeTrue();
        (await Repo.RecordAccumulatorReviewAsync(Tenant, "C1", Review("commit-C1", 7m))).Should().BeTrue(); // same commit: kept
        (await StoredAsync("C1")).AccumulatorClampReview!.Clamps.Single().Applied.Should().Be(50m);

        (await Repo.RecordAccumulatorReviewAsync(Tenant, "C1", Review("commit-C1-again", 20m))).Should().BeTrue();
        var stored = (await StoredAsync("C1")).AccumulatorClampReview!;
        stored.CommitId.Should().Be("commit-C1-again");
        stored.Kind.Should().Be(AccumulatorClampReview.Clamped);
        (await Repo.RecordAccumulatorReviewAsync(Tenant, "missing", Review("x", 1m))).Should().BeFalse();
    }

    /// <summary>S3: the void write carries the terminal reversal (same write).</summary>
    [Fact]
    public async Task TheVoidWrite_CarriesTheReversal()
    {
        var claim = await SeedAsync("C1", ClaimStatus.Paid);
        var reversal = AccumulatorOutboxItem.ForReversal(claim, Now)!;

        (await Repo.MarkVoidedProjectionAsync(Tenant, "C1", Now, "operator", default, reversal)).Should().BeTrue();

        var stored = await StoredAsync("C1");
        stored.Status.Should().Be(ClaimStatus.Voided);
        stored.PendingAccumulatorReversal!.Id.Should().Be(reversal.Id);
        stored.PendingAccumulatorReversal.Reversal!.ClaimId.Should().Be("C1");
        stored.PendingAccumulatorReversal.Reversal.BenefitPlanId.Should().Be(PlanId);
    }

    [Fact]
    public async Task ThePendCodeQuery_FindsOnlyThatPend()
    {
        await SeedAsync("P1", ClaimStatus.Pended);
        await SeedAsync("P2", ClaimStatus.Pended);
        await SeedAsync("A1", ClaimStatus.Approved);
        var p1 = await StoredAsync("P1");
        p1.PendDetails = new PendDetails { PendCode = "ACCUMCOMMIT", PendReason = "x" };
        await Repo.UpdateAdjudicationProjectionAsync(Tenant, "P1", new AdjudicationResult(), [], pendDetails: p1.PendDetails, isPend: true);
        await Repo.UpdateAdjudicationProjectionAsync(Tenant, "P2", new AdjudicationResult(), [],
            pendDetails: new PendDetails { PendCode = "NCCI", PendReason = "y" }, isPend: true);

        (await Repo.FindPendedByPendCodeAsync("ACCUMCOMMIT", 10)).Select(c => c.Id).Should().BeEquivalentTo(["P1"]);
    }

    /// <summary>
    /// #1299: an approval re-run that ends Deny on a Pended claim writes its
    /// fenced projection; the status write is blocked (a pend is never
    /// downgraded by the projection) and the contradictory-status repair it
    /// then attempts must not fail.
    /// </summary>
    [Fact]
    public async Task AFencedDenyProjection_OnAPendedClaim_IsWritten_AndLeavesItPended()
    {
        await SeedAsync("PD", ClaimStatus.Pended);
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "PD", "tok-1", "examiner", DateTime.UtcNow, TimeSpan.FromMinutes(5))).Should().BeTrue();

        var written = await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "PD",
            new AdjudicationResult { DenialReasonCode = "197", DenialReason = "Prior authorization required but not provided" },
            [], resolvedStatus: ClaimStatus.Denied, requiredResolutionLockToken: "tok-1");

        written.Should().BeTrue();
        var stored = await StoredAsync("PD");
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.AdjudicationResult!.DenialReasonCode.Should().Be("197");
    }

    /// <summary>
    /// #1299 nit: the bulk re-runs take the least recently pended first. A
    /// re-run that pends again re-stamps PendedAt, so it goes to the back of
    /// the queue instead of refilling every batch (oldest-submitted-first did).
    /// </summary>
    [Fact]
    public async Task ThePendCodeQuery_ReturnsTheLeastRecentlyPendedFirst()
    {
        await SeedAsync("RETRIED", ClaimStatus.Pended);
        await SeedAsync("WAITING", ClaimStatus.Pended);
        await Repo.UpdateAdjudicationProjectionAsync(Tenant, "RETRIED", new AdjudicationResult(), [],
            pendDetails: new PendDetails { PendCode = "PAUNAVAIL", PendReason = "x", PendedAt = Now.AddHours(2) }, isPend: true);
        await Repo.UpdateAdjudicationProjectionAsync(Tenant, "WAITING", new AdjudicationResult(), [],
            pendDetails: new PendDetails { PendCode = "PAUNAVAIL", PendReason = "y", PendedAt = Now.AddHours(1) }, isPend: true);

        (await Repo.FindPendedByPendCodeAsync("PAUNAVAIL", 1)).Select(c => c.Id).Should().Equal("WAITING");
        (await Repo.FindPendedByPendCodeAsync("PAUNAVAIL", 10)).Select(c => c.Id).Should().Equal("WAITING", "RETRIED");
    }

    /// <summary>
    /// #1299 re-verify S1: claims with additional pend reasons are left for an
    /// examiner by the bulk re-runs (Skipped, never re-stamped). They must not
    /// be returned, or <c>limit</c> of them at the head would fill every batch
    /// forever and the re-runnable claim behind them would never be reached.
    /// </summary>
    [Fact]
    public async Task ThePendCodeQuery_ExcludesClaimsWithOtherPendReasons()
    {
        for (var i = 1; i <= 3; i++)
        {
            await SeedAsync("MIXED" + i, ClaimStatus.Pended);
            var mixed = new PendDetails { PendCode = "PAUNAVAIL", PendReason = "x", PendedAt = Now.AddMinutes(i) };
            mixed.AdditionalPendReasons.Add("NCCI: bundled pair");
            await Repo.UpdateAdjudicationProjectionAsync(Tenant, "MIXED" + i, new AdjudicationResult(), [], pendDetails: mixed, isPend: true);
        }
        await SeedAsync("ONLY", ClaimStatus.Pended);
        await Repo.UpdateAdjudicationProjectionAsync(Tenant, "ONLY", new AdjudicationResult(), [],
            pendDetails: new PendDetails { PendCode = "PAUNAVAIL", PendReason = "y", PendedAt = Now.AddHours(1) }, isPend: true);

        (await Repo.FindPendedByPendCodeAsync("PAUNAVAIL", 2)).Select(c => c.Id).Should().Equal("ONLY");
    }

    [Fact]
    public async Task TheSweepQuery_ReturnsFinalizedClaimsWithNoPendingCommit_InTheWindow()
    {
        await SeedAsync("A1", ClaimStatus.Approved, deductible: 10m, oop: 10m);
        await SeedAsync("PAID", ClaimStatus.Paid, deductible: 10m, oop: 10m);
        await SeedAsync("ADJ", ClaimStatus.Approved, ClaimVersionState.Adjusted, deductible: 10m, oop: 10m);
        await SeedAsync("PEND", ClaimStatus.Pended, deductible: 10m, oop: 10m);
        await SeedAsync("WAITING");
        await FinalizeAsync("WAITING", ClaimStatus.Approved, Item("WAITING"));

        var now = DateTime.UtcNow;
        var found = await Repo.FindFinalizedForAccumulatorSweepAsync(now.AddHours(-1), now.AddHours(1), 100);

        found.Select(c => c.Id).Should().BeEquivalentTo(["A1", "PAID"]);
        (await Repo.FindFinalizedForAccumulatorSweepAsync(now.AddHours(1), now.AddHours(2), 100)).Should().BeEmpty();
    }

    // ── accumulator totals (Redis rebuild source) ───────────────────────

    [Fact]
    public async Task Totals_ExcludeSupersededVersions_AndListEachCountedClaim()
    {
        await SeedAsync("A1", ClaimStatus.Approved, deductible: 100m, oop: 120m);
        await SeedAsync("A2", ClaimStatus.Paid, deductible: 50m, oop: 50m);
        await SeedAsync("ADJ", ClaimStatus.Approved, ClaimVersionState.Adjusted, deductible: 400m, oop: 400m);
        await SeedAsync("SUP", ClaimStatus.Approved, deductible: 300m, oop: 300m, supersededAt: Now);
        await SeedAsync("PEND", ClaimStatus.Pended, deductible: 999m, oop: 999m);

        var totals = await Repo.GetAccumulatorTotalsAsync("M1", "Individual", PlanId, "2026");

        totals.Totals.Should().Contain(t => t.AccumulatorType == "IndividualDeductible" && t.AccumulatedAmount == 150m);
        totals.Totals.Should().Contain(t => t.AccumulatorType == "IndividualOutOfPocketMax" && t.AccumulatedAmount == 170m);
        totals.Claims.Select(c => c.ClaimId).Should().BeEquivalentTo(["A1", "A2"]);
        totals.Claims.Single(c => c.ClaimId == "A1").Totals.Should().Contain(t =>
            t.AccumulatorType == "IndividualDeductible" && t.NetworkTier == "InNetwork" && t.AccumulatedAmount == 100m);
        totals.Claims.Single(c => c.ClaimId == "A1").Totals.Should().Contain(t =>
            t.AccumulatorType == "IndividualOutOfPocketMax" && t.AccumulatedAmount == 120m);

        var family = await Repo.GetAccumulatorTotalsAsync("S1", "Family", PlanId, "2026");
        family.Totals.Should().Contain(t => t.AccumulatorType == "FamilyDeductible" && t.AccumulatedAmount == 150m);
        family.Claims.Should().HaveCount(2);
    }
}

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimRepositoryAccumulatorOutboxMongoTests : ClaimRepositoryAccumulatorOutboxTests, IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public ClaimRepositoryAccumulatorOutboxMongoTests(MongoRunnerFixture mongo) => _mongo = mongo;

    protected override IClaimRepository Repo
    {
        get
        {
            var ctx = new DefaultHttpContext();
            ctx.Items["TenantId"] = Tenant;
            return new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx },
                NullLogger<ClaimRepositoryMongo>.Instance);
        }
    }

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_outbox_test");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);
}
