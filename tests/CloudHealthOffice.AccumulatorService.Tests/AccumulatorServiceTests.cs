using AccumulatorService.Models;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// Behavior of the accumulator domain service. Covers:
///   - Apply path: sums deltas into the correct snapshot
///   - Idempotency: replay of the same ClaimFinalizedEvent does not double-count
///   - Retro adjustment: a prior-year service date targets the prior-year snapshot
///   - Orphan: unknown plan year emits OrphanAccumulatorClaimEvent and skips
///   - Manual adjustment: produces audit event + AccumulatorAdjusted publish
///   - Family aggregate flag
///   - Multi-line per-category attribution
/// </summary>
public class AccumulatorServiceTests
{
    private const string Tenant = "t1";
    private const string Member = "m-100";

    private static global::AccumulatorService.Services.AccumulatorService BuildSut(
        out InMemoryAccumulatorRepository repo,
        out InMemoryProcessedClaimStore processed,
        out RecordingPublisher publisher)
    {
        repo = new InMemoryAccumulatorRepository();
        processed = new InMemoryProcessedClaimStore();
        publisher = new RecordingPublisher();
        return new global::AccumulatorService.Services.AccumulatorService(
            repo, processed, publisher,
            NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);
    }

    private static AccumulatorSnapshot SeedSnapshot(InMemoryAccumulatorRepository repo, int year, decimal dedLimit = 2000m, decimal oopLimit = 8000m)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var s = new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(Tenant, Member, start),
            TenantId = Tenant,
            MemberId = Member,
            PlanYearStart = start,
            PlanYearEnd = end,
            IndividualDeductibleLimit = dedLimit,
            IndividualOopLimit = oopLimit,
            FamilyDeductibleLimit = dedLimit * 3,
            FamilyOopLimit = oopLimit * 2
        };
        repo.Seed(s);
        return s;
    }

    private static ClaimFinalizedEvent MakeClaim(
        string claimId,
        DateTime serviceDate,
        decimal deductible,
        decimal coinsurance = 0m,
        decimal copay = 0m,
        decimal oop = 0m,
        bool familyAggregate = false,
        string category = "PrimaryCare",
        List<ClaimFinalizedLineItem>? lines = null) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        ClaimId = claimId,
        ClaimNumber = claimId,
        MemberId = Member,
        ServiceDate = serviceDate,
        AdjudicationTimestamp = DateTimeOffset.UtcNow,
        BenefitCategory = category,
        IsFamilyAggregate = familyAggregate,
        DeductibleApplied = deductible,
        CoinsuranceApplied = coinsurance,
        CopayApplied = copay,
        OopApplied = oop == 0m ? deductible + coinsurance + copay : oop,
        PlanPaid = 0m,
        MemberResponsibility = deductible + coinsurance + copay,
        LineItems = lines ?? new()
    };

    [Fact]
    public async Task Apply_UpdatesCorrectSnapshotAndAuditEvent()
    {
        var sut = BuildSut(out var repo, out var processed, out var pub);
        SeedSnapshot(repo, 2026);

        var evt = MakeClaim("CLM-1", new DateTime(2026, 3, 15), deductible: 150m, coinsurance: 50m, oop: 200m);
        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(150m, result.Snapshot!.IndividualDeductibleUsed);
        Assert.Equal(200m, result.Snapshot.IndividualOopUsed);
        Assert.Equal(1, result.Snapshot.Version);

        Assert.Single(repo.Events);
        var audit = repo.Events[0];
        Assert.Equal("ClaimApplied", audit.EventType);
        Assert.Equal("CLM-1", audit.SourceClaimId);
        // Audit row gets a fresh EventId per attempt (not the wire id) so a retry
        // after a transient failure doesn't collide on (tenantId, eventId).
        Assert.False(string.IsNullOrWhiteSpace(audit.EventId));
        Assert.Equal(audit.Version, result.Snapshot.Version);

        Assert.Single(pub.Adjusted);
        Assert.Equal("ClaimApplied", pub.Adjusted[0].AdjustmentSource);
    }

    [Fact]
    public async Task Apply_IsIdempotent_SecondReplayReturnsDuplicate()
    {
        var sut = BuildSut(out var repo, out var processed, out var pub);
        SeedSnapshot(repo, 2026);

        var evt = MakeClaim("CLM-DUP", new DateTime(2026, 3, 15), deductible: 100m);
        var first = await sut.ApplyClaimFinalizedAsync(evt);

        // Regenerate a fresh EventId on the replay — dedup is keyed by ClaimId, not EventId.
        var replay = MakeClaim("CLM-DUP", new DateTime(2026, 3, 15), deductible: 100m);
        var second = await sut.ApplyClaimFinalizedAsync(replay);

        Assert.Equal(ApplyOutcome.Applied, first.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, second.Outcome);

        var snap = await repo.GetSnapshotAsync(Tenant, Member, new DateTime(2026, 1, 1));
        Assert.Equal(100m, snap!.IndividualDeductibleUsed); // not 200
        Assert.Single(repo.Events);

        var marker = await processed.GetAsync(Tenant, "CLM-DUP");
        Assert.NotNull(marker);
        Assert.Equal("Applied", marker!.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(marker.ResultingEventId));
    }

    [Fact]
    public async Task Apply_RetroClaim_TargetsPriorYearSnapshotNotCurrent()
    {
        var sut = BuildSut(out var repo, out _, out _);
        var priorYear = SeedSnapshot(repo, 2025);
        var currentYear = SeedSnapshot(repo, 2026);

        // Finalized today, but service rendered in the prior plan year.
        var evt = MakeClaim("CLM-RETRO", new DateTime(2025, 11, 20), deductible: 300m);
        evt.AdjudicationTimestamp = DateTimeOffset.UtcNow;

        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var prior = await repo.GetSnapshotAsync(Tenant, Member, priorYear.PlanYearStart);
        var current = await repo.GetSnapshotAsync(Tenant, Member, currentYear.PlanYearStart);
        Assert.Equal(300m, prior!.IndividualDeductibleUsed);
        Assert.Equal(0m, current!.IndividualDeductibleUsed);
    }

    [Fact]
    public async Task Apply_OrphanServiceDate_EmitsOrphanEventAndSkips()
    {
        var sut = BuildSut(out var repo, out var processed, out var pub);
        SeedSnapshot(repo, 2026);

        // Service date predates any known snapshot and the event does not carry
        // a producer-asserted plan year that would cover it.
        var evt = MakeClaim("CLM-ORPHAN", new DateTime(2022, 4, 1), deductible: 250m);
        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Orphan, result.Outcome);
        Assert.Empty(repo.Events);
        Assert.Single(pub.Orphans);
        Assert.Equal("CLM-ORPHAN", pub.Orphans[0].ClaimId);

        var marker = await processed.GetAsync(Tenant, "CLM-ORPHAN");
        Assert.Equal("OrphanSkipped", marker!.Outcome);
    }

    [Fact]
    public async Task Apply_FamilyAggregate_UpdatesFamilyCountersToo()
    {
        var sut = BuildSut(out var repo, out _, out _);
        SeedSnapshot(repo, 2026);

        var evt = MakeClaim("CLM-FAM", new DateTime(2026, 2, 10), deductible: 500m, oop: 500m, familyAggregate: true);
        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal(500m, result.Snapshot!.IndividualDeductibleUsed);
        Assert.Equal(500m, result.Snapshot.FamilyDeductibleUsed);
        Assert.Equal(500m, result.Snapshot.FamilyOopUsed);
    }

    [Fact]
    public async Task Apply_MultiLineClaim_AttributesPerCategory()
    {
        var sut = BuildSut(out var repo, out _, out _);
        SeedSnapshot(repo, 2026);

        var evt = MakeClaim("CLM-MULTI", new DateTime(2026, 4, 1), deductible: 0m, category: "PrimaryCare", lines: new List<ClaimFinalizedLineItem>
        {
            new() { LineNumber = 1, BenefitCategory = "PrimaryCare", ServiceCode = "99213", CopayApplied = 25m, OopApplied = 25m },
            new() { LineNumber = 2, BenefitCategory = "Lab", ServiceCode = "80053", CoinsuranceApplied = 12m, OopApplied = 12m }
        });
        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var snap = result.Snapshot!;
        Assert.Equal(37m, snap.IndividualOopUsed);
        Assert.Equal(2, snap.ServiceAccumulators.Count);
        Assert.Contains(snap.ServiceAccumulators, s => s.BenefitCategory == "PrimaryCare" && s.Used == 25m);
        Assert.Contains(snap.ServiceAccumulators, s => s.BenefitCategory == "Lab" && s.Used == 12m);
    }

    [Fact]
    public async Task Apply_PendingMarkerFromPriorCrash_IsRetriedNotSkipped()
    {
        // Simulate: a prior attempt inserted the Pending marker but crashed before
        // CompleteAsync. The next delivery must re-enter the apply path, not treat
        // the claim as already applied.
        var sut = BuildSut(out var repo, out var processed, out _);
        SeedSnapshot(repo, 2026);

        // Pre-seed a Pending marker as if a crash occurred.
        await processed.TryBeginAsync("t1", "CLM-CRASH");
        var marker = await processed.GetAsync("t1", "CLM-CRASH");
        Assert.Equal("Pending", marker!.Outcome);

        var evt = MakeClaim("CLM-CRASH", new DateTime(2026, 6, 1), deductible: 120m);
        var result = await sut.ApplyClaimFinalizedAsync(evt);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var snap = await repo.GetSnapshotAsync("t1", "m-100", new DateTime(2026, 1, 1));
        Assert.Equal(120m, snap!.IndividualDeductibleUsed);

        var final = await processed.GetAsync("t1", "CLM-CRASH");
        Assert.Equal("Applied", final!.Outcome);
    }

    [Fact]
    public async Task Adjust_SameAdjustmentId_IsIdempotentAndReturnsExistingSnapshot()
    {
        var sut = BuildSut(out var repo, out _, out var pub);
        SeedSnapshot(repo, 2026);

        var req = new AccumulatorAdjustmentRequest
        {
            AdjustmentId = "adj-42",
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            ActorId = "ops-user@cho",
            Reason = "Out-of-system payment posted manually",
            DeductibleDelta = 200m
        };
        var first = await sut.AdjustAsync("t1", "m-100", req);
        var second = await sut.AdjustAsync("t1", "m-100", req);

        Assert.Equal("adj-42", first.AdjustmentId);
        Assert.Equal("adj-42", second.AdjustmentId);
        Assert.Equal(first.Snapshot.Version, second.Snapshot.Version);
        Assert.Equal(200m, second.Snapshot.IndividualDeductibleUsed);
        Assert.Single(repo.Events); // only one ManualAdjustment row
        Assert.Single(pub.Adjusted); // only one publish
    }

    [Fact]
    public async Task Adjust_ManualAdjustment_RecordsAuditAndPublishes()
    {
        var sut = BuildSut(out var repo, out _, out var pub);
        SeedSnapshot(repo, 2026);

        var req = new AccumulatorAdjustmentRequest
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            ActorId = "ops-user@cho",
            Reason = "Correct mis-keyed claim CLM-99 (applied under wrong member)",
            DeductibleDelta = -100m,
            OopDelta = -100m
        };
        var result = await sut.AdjustAsync(Tenant, Member, req);

        Assert.False(string.IsNullOrWhiteSpace(result.AdjustmentId));
        Assert.Equal(0m, result.Snapshot.IndividualDeductibleUsed); // floored at 0

        var audit = repo.Events.Single();
        Assert.Equal("ManualAdjustment", audit.EventType);
        Assert.Equal("ops-user@cho", audit.ActorId);
        Assert.Equal(req.Reason, audit.Reason);

        var adjusted = pub.Adjusted.Single();
        Assert.Equal("ManualAdjustment", adjusted.AdjustmentSource);
        Assert.Equal(req.Reason, adjusted.Reason);
    }

    [Fact]
    public async Task TenantIsolation_ClaimForOneTenantDoesNotTouchAnother()
    {
        var sut = BuildSut(out var repo, out _, out _);
        SeedSnapshot(repo, 2026);

        // Seed a snapshot under a different tenant id with the same member id to
        // verify partition isolation.
        repo.Seed(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId("other-tenant", Member, new DateTime(2026, 1, 1)),
            TenantId = "other-tenant",
            MemberId = Member,
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = 2000m
        });

        var evt = MakeClaim("CLM-TENANT", new DateTime(2026, 5, 5), deductible: 400m);
        await sut.ApplyClaimFinalizedAsync(evt);

        var mine = await repo.GetSnapshotAsync(Tenant, Member, new DateTime(2026, 1, 1));
        var other = await repo.GetSnapshotAsync("other-tenant", Member, new DateTime(2026, 1, 1));
        Assert.Equal(400m, mine!.IndividualDeductibleUsed);
        Assert.Equal(0m, other!.IndividualDeductibleUsed);
    }
}
