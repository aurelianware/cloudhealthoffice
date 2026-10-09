using AccumulatorService.Models;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// PR #1278 re-review N5: applies and reversals happen exactly once, even
/// when workers race or crash. (a) A Pending marker within its lease means
/// an attempt is in flight; snapshot writes are version-conditional; at most
/// one reversal per claim. (b) Reversing a legacy row (requested, not
/// clamped, deltas) replays the event log instead of over-reversing.
/// (c) A frequency-8 void reverses the original, not itself.
/// </summary>
public class AccumulatorExactlyOnceTests
{
    private const string Tenant = "t1";
    private const string Member = "m-300";
    private static readonly DateTime YearStart = new(2026, 1, 1);

    private sealed record World(
        InMemoryAccumulatorRepository Repo,
        InMemoryProcessedClaimStore Processed,
        RecordingPublisher Pub)
    {
        public global::AccumulatorService.Services.AccumulatorService Service(InMemoryProcessedClaimStore? processed = null) =>
            new(Repo, processed ?? Processed, Pub,
                NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);

        public async Task<AccumulatorSnapshot> Snapshot() =>
            (await Repo.GetSnapshotAsync(Tenant, Member, YearStart))!;
    }

    private static World Build(decimal dedLimit = 2000m)
    {
        var w = new World(new InMemoryAccumulatorRepository(), new InMemoryProcessedClaimStore(), new RecordingPublisher());
        w.Repo.Seed(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(Tenant, Member, YearStart),
            TenantId = Tenant,
            MemberId = Member,
            PlanYearStart = YearStart,
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = dedLimit,
            IndividualOopLimit = 8000m,
            FamilyDeductibleLimit = dedLimit * 3,
            FamilyOopLimit = 16000m,
        });
        return w;
    }

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

    // ── (a) in-flight markers ─────────────────────────────────────────

    [Fact]
    public async Task PendingMarkerWithinLease_IsInProgress_AndWritesNothing()
    {
        var w = Build();
        await w.Processed.TryBeginAsync(Tenant, "C1"); // another worker, mid-flight

        var result = await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 100m));

        Assert.Equal(ApplyOutcome.InProgress, result.Outcome);
        Assert.Empty(w.Repo.Events);
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);
    }

    /// <summary>
    /// The double-reversal the review found: a reversal still in flight
    /// (its marker Pending) used to be re-entered by a second delivery, and
    /// both backed the claim out. Now the second waits; after the lease it
    /// takes over and reverses once.
    /// </summary>
    [Fact]
    public async Task ReversalInFlight_SecondDeliveryWaits_ThenReversesOnce()
    {
        var w = Build();
        var sut = w.Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await w.Processed.TryBeginAsync(Tenant, "C1:reversal"); // in flight elsewhere

        var waiting = await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m, status: "Reversed"));
        Assert.Equal(ApplyOutcome.InProgress, waiting.Outcome);
        Assert.Equal(100m, (await w.Snapshot()).IndividualDeductibleUsed);

        w.Processed.Now += w.Processed.Lease + TimeSpan.FromSeconds(1);
        var taken = await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m, status: "Reversed"));
        var again = await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Applied, taken.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, again.Outcome);
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Single(w.Repo.Events, e => e.EventType == "ClaimReversed");
    }

    /// <summary>
    /// Two workers both past the marker (a lease taken over from a worker
    /// that was slow, not dead): the second reverses while the first is
    /// about to write. The first loses the version race, re-reads, sees the
    /// reversal and stops — the claim is reversed once.
    /// </summary>
    [Fact]
    public async Task ConcurrentReversals_PastTheMarker_ReverseOnce()
    {
        var w = Build();
        await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await w.Service().ApplyClaimFinalizedAsync(Claim("C2", 50m));

        var other = w.Service(new InMemoryProcessedClaimStore());
        w.Repo.BeforeNextAppend = async _ =>
            Assert.Equal(ApplyOutcome.Applied,
                (await other.ApplyClaimFinalizedAsync(Claim("C1", 100m, status: "Reversed"))).Outcome);

        var first = await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 100m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Duplicate, first.Outcome);
        Assert.Equal("DuplicateReversal", first.Reason);
        Assert.Equal(50m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Single(w.Repo.Events, e => e.EventType == "ClaimReversed");
    }

    /// <summary>Version-conditional writes: a claim applied while another is mid-write is not lost.</summary>
    [Fact]
    public async Task ConcurrentApplies_BothCount()
    {
        var w = Build();
        var other = w.Service(new InMemoryProcessedClaimStore());
        w.Repo.BeforeNextAppend = async _ => await other.ApplyClaimFinalizedAsync(Claim("C2", 30m));

        var first = await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 100m));

        Assert.Equal(ApplyOutcome.Applied, first.Outcome);
        var snap = await w.Snapshot();
        Assert.Equal(130m, snap.IndividualDeductibleUsed);
        Assert.Equal(2, snap.Version);
        Assert.Equal(new long[] { 1, 2 }, w.Repo.Events.Select(e => e.Version).OrderBy(v => v));
    }

    /// <summary>
    /// A crash between the event append and the snapshot write: the row is
    /// the truth. The next write projects it first; the crashed claim's
    /// redelivery (after the lease) finds its row and does not count twice.
    /// </summary>
    [Fact]
    public async Task CrashAfterAppend_RowIsProjectedOnce()
    {
        var w = Build();
        var sut = w.Service();
        w.Repo.ThrowOnNextSnapshotWrite = true;
        await Assert.ThrowsAsync<IOException>(() => sut.ApplyClaimFinalizedAsync(Claim("C1", 100m)));
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 30m));
        Assert.Equal(130m, (await w.Snapshot()).IndividualDeductibleUsed);

        w.Processed.Now += w.Processed.Lease + TimeSpan.FromSeconds(1);
        var redelivered = await sut.ApplyClaimFinalizedAsync(Claim("C1", 100m));

        Assert.Equal(ApplyOutcome.Duplicate, redelivered.Outcome);
        Assert.Equal(130m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Equal("Applied", (await w.Processed.GetAsync(Tenant, "C1"))!.Outcome);
    }

    // ── (b) legacy rows ───────────────────────────────────────────────

    /// <summary>
    /// The reviewer's case: limit 500, used 450, a legacy claim requested 200
    /// — the snapshot took 50 but the row (written before rows recorded the
    /// clamped amounts) says 200. Voiding it must leave 450, not 300.
    /// </summary>
    [Fact]
    public async Task LegacyRow_VoidReplaysTheLog_LeavesUsed450()
    {
        var w = Build(dedLimit: 500m);
        var sut = w.Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 450m));

        // What the old code wrote for C2: the requested 200 on the row, the
        // clamped 500 on the snapshot.
        var snap = await w.Snapshot();
        w.Repo.AddEvent(new AccumulatorEvent
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = Tenant,
            AggregateId = snap.Id,
            Version = 2,
            MemberId = Member,
            PlanYearStart = snap.PlanYearStart,
            PlanYearEnd = snap.PlanYearEnd,
            EventType = "ClaimApplied",
            SourceClaimId = "C2",
            SourceReference = "C2",
            DeductibleDelta = 200m,
            OopDelta = 200m,
            DeltasClamped = false,
        });
        snap.IndividualDeductibleUsed = 500m;
        snap.IndividualOopUsed = 650m;
        snap.Version = 2;
        w.Repo.Overwrite(snap);

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 200m, status: "Reversed"));

        var after = await w.Snapshot();
        Assert.Equal(450m, after.IndividualDeductibleUsed);
        Assert.Equal(450m, after.IndividualOopUsed);
        var reversal = w.Repo.Events.Single(e => e.EventType == "ClaimReversed");
        Assert.Equal(-50m, reversal.DeductibleDelta);
        Assert.Equal(-200m, reversal.OopDelta);
    }

    [Fact]
    public void Replay_LegacyRowUnderTheLimit_IsTheRecordedAmount()
    {
        var target = new AccumulatorEvent { Id = "x", EventType = "ClaimApplied", DeductibleDelta = 120m };
        var log = new List<AccumulatorEvent>
        {
            new() { Id = "a", EventType = "ClaimApplied", DeductibleDelta = 100m, DeltasClamped = true },
            target,
        };

        Assert.Equal(120m, global::AccumulatorService.Services.AccumulatorService.Replay(
            log, target, current: 220m, limit: 500m, e => e.DeductibleDelta));
    }

    [Fact]
    public async Task NewRows_AreMarkedClamped()
    {
        var w = Build();
        await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 10m));
        Assert.True(w.Repo.Events.Single().DeltasClamped);
    }

    // ── round 3, B3: a replacement overtaking its original ────────────

    /// <summary>
    /// The reviewer's order: Kafka is keyed on the claim id, so replacement
    /// C2 (frequency 7 of C1) is applied before C1's own apply arrives, and
    /// C1's void comes last. C2's reversal of C1 found nothing and used to
    /// complete as "nothing to reverse"; C1 then applied on top of C2 —
    /// deductible 600 instead of 300. Now it leaves a tombstone and C1's
    /// apply is skipped.
    /// </summary>
    [Fact]
    public async Task ReplacementBeforeOriginal_OriginalApplyIsSkipped_CountsOnce()
    {
        var w = Build();
        var sut = w.Service();

        var c2 = await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));
        var c1 = await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m));
        var c1Void = await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Applied, c2.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, c1.Outcome);
        Assert.Equal(global::AccumulatorService.Services.AccumulatorService.ReversedBeforeApplyOutcome, c1.Reason);
        Assert.Equal(ApplyOutcome.Duplicate, c1Void.Outcome);
        var snap = await w.Snapshot();
        Assert.Equal(300m, snap.IndividualDeductibleUsed);
        Assert.Equal(300m, snap.IndividualOopUsed);
        Assert.DoesNotContain(w.Repo.Events, e => e.SourceClaimId == "C1");
    }

    /// <summary>
    /// The original's apply is in flight when the replacement arrives: the
    /// replacement waits (InProgress, nothing written, its reversal marker
    /// released), then reverses the original once it has applied.
    /// </summary>
    [Fact]
    public async Task ReplacementWhileOriginalApplyInFlight_Waits_ThenReplaces()
    {
        var w = Build();
        var sut = w.Service();
        await w.Processed.TryBeginAsync(Tenant, "C1"); // C1's apply, mid-flight

        var waiting = await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.InProgress, waiting.Outcome);
        Assert.Empty(w.Repo.Events);
        Assert.Null(await w.Processed.GetAsync(Tenant, "C1:reversal"));

        // C1's apply completes (lease taken over by its redelivery).
        w.Processed.Now += w.Processed.Lease + TimeSpan.FromSeconds(1);
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 300m));
        var retried = await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, frequency: "7", original: "C1"));

        Assert.Equal(ApplyOutcome.Applied, retried.Outcome);
        Assert.Equal(300m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Single(w.Repo.Events, e => e.EventType == "ClaimReversed" && e.SourceClaimId == "C1");
    }

    // ── round 3, H4: denied claims ────────────────────────────────────

    /// <summary>
    /// A claim pended after benefit calculation carries priced amounts; an
    /// examiner's denial finalizes it as Denied. Nothing is applied.
    /// </summary>
    [Fact]
    public async Task DeniedClaim_IsNotApplied()
    {
        var w = Build();

        var result = await w.Service().ApplyClaimFinalizedAsync(Claim("C1", 120m, status: "Denied"));

        Assert.Equal(ApplyOutcome.Skipped, result.Outcome);
        Assert.Empty(w.Repo.Events);
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Equal(global::AccumulatorService.Services.AccumulatorService.DeniedOutcome,
            (await w.Processed.GetAsync(Tenant, "C1"))!.Outcome);
    }

    /// <summary>A claim applied and later finalized as Denied is backed out.</summary>
    [Fact]
    public async Task AppliedThenDenied_IsReversed()
    {
        var w = Build();
        var sut = w.Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 120m));

        var result = await sut.ApplyClaimFinalizedAsync(Claim("C1", 120m, status: "Denied"));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);
    }

    // ── (c) frequency-8 void ──────────────────────────────────────────

    /// <summary>
    /// A frequency-8 void claim arrives with FinalStatus "Reversed". It used
    /// to be treated as a reversal of itself (nothing to reverse) and the
    /// original stayed counted.
    /// </summary>
    [Fact]
    public async Task Frequency8Void_WithReversedStatus_ReversesTheOriginal()
    {
        var w = Build();
        var sut = w.Service();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 80m));

        var result = await sut.ApplyClaimFinalizedAsync(
            Claim("C1-VOID", 80m, status: "Reversed", frequency: "8", original: "C1"));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal(0m, (await w.Snapshot()).IndividualDeductibleUsed);
        Assert.Equal("C1", w.Repo.Events.Single(e => e.EventType == "ClaimReversed").SourceClaimId);
    }
}
