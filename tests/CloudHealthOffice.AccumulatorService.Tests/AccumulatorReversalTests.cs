using AccumulatorService.Models;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// Voids and replacements back out what a claim applied (including a NAIC
/// COB deductible credit), exactly once.
/// </summary>
public class AccumulatorReversalTests
{
    private const string Tenant = "t1";
    private const string Member = "m-200";

    private static (global::AccumulatorService.Services.AccumulatorService Sut, InMemoryAccumulatorRepository Repo, RecordingPublisher Pub) Build(
        decimal dedLimit = 2000m)
    {
        var repo = new InMemoryAccumulatorRepository();
        var pub = new RecordingPublisher();
        var sut = new global::AccumulatorService.Services.AccumulatorService(
            repo, new InMemoryProcessedClaimStore(), pub,
            NullLogger<global::AccumulatorService.Services.AccumulatorService>.Instance);
        repo.Seed(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(Tenant, Member, new DateTime(2026, 1, 1)),
            TenantId = Tenant,
            MemberId = Member,
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = dedLimit,
            IndividualOopLimit = 8000m,
            FamilyDeductibleLimit = dedLimit * 3,
            FamilyOopLimit = 16000m,
        });
        return (sut, repo, pub);
    }

    private static ClaimFinalizedEvent Claim(
        string claimId, decimal deductible, decimal? credited = null, string status = "Paid",
        string? frequency = null, string? original = null) => new()
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
        DeductibleCredited = credited,
        OopApplied = deductible,
        MemberResponsibility = deductible,
    };

    private static async Task<AccumulatorSnapshot> Snapshot(InMemoryAccumulatorRepository repo) =>
        (await repo.GetSnapshotAsync(Tenant, Member, new DateTime(2026, 1, 1)))!;

    [Fact]
    public async Task Void_RestoresTheAccumulators_IncludingTheNaicCredit()
    {
        var (sut, repo, pub) = Build();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", deductible: 30m, credited: 100m));
        Assert.Equal(100m, (await Snapshot(repo)).IndividualDeductibleUsed);
        Assert.Equal(30m, (await Snapshot(repo)).IndividualOopUsed);

        var result = await sut.ApplyClaimFinalizedAsync(Claim("C1", 30m, 100m, status: "Reversed"));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var snap = await Snapshot(repo);
        Assert.Equal(0m, snap.IndividualDeductibleUsed);
        Assert.Equal(0m, snap.IndividualOopUsed);
        Assert.Equal(0m, snap.ServiceAccumulators.Single().Used);
        var reversal = repo.Events.Single(e => e.EventType == "ClaimReversed");
        Assert.Equal(-100m, reversal.DeductibleDelta);
        Assert.Equal("ClaimReversed", pub.Adjusted.Last().AdjustmentSource);
    }

    [Fact]
    public async Task Void_IsIdempotent_AndAVoidOfANeverAppliedClaimReversesNothing()
    {
        var (sut, repo, _) = Build();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 50m));

        await sut.ApplyClaimFinalizedAsync(Claim("C1", 50m, status: "Reversed"));
        var again = await sut.ApplyClaimFinalizedAsync(Claim("C1", 50m, status: "Reversed"));
        Assert.Equal(ApplyOutcome.Duplicate, again.Outcome);

        var never = await sut.ApplyClaimFinalizedAsync(Claim("C-NEVER", 50m, status: "Reversed"));
        // Round 3 (B3): never seen — a tombstone, so a late apply is skipped.
        Assert.Equal("ReversedBeforeApply", never.Reason);
        Assert.Equal(ApplyOutcome.Duplicate, (await sut.ApplyClaimFinalizedAsync(Claim("C-NEVER", 50m))).Outcome);
        Assert.Equal(0m, (await Snapshot(repo)).IndividualDeductibleUsed);
    }

    /// <summary>
    /// A replacement (CLM05-3 = 7) naming its original: the original's deltas
    /// are reversed before the replacement's are applied, so the deductible
    /// counts once — and the original's own later void event does not reverse
    /// it a second time.
    /// </summary>
    [Fact]
    public async Task Replacement_ReversesTheOriginal_NoDoubleCount()
    {
        var (sut, repo, _) = Build();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", deductible: 30m, credited: 100m));

        await sut.ApplyClaimFinalizedAsync(Claim("C2", deductible: 30m, credited: 100m, frequency: "7", original: "C1"));
        Assert.Equal(100m, (await Snapshot(repo)).IndividualDeductibleUsed);
        Assert.Equal(30m, (await Snapshot(repo)).IndividualOopUsed);

        // The reversal run later voids C1: already reversed, no change.
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 30m, 100m, status: "Reversed"));
        Assert.Equal(100m, (await Snapshot(repo)).IndividualDeductibleUsed);
    }

    [Fact]
    public async Task VoidClaim_Frequency8_ReversesTheOriginalOnly()
    {
        var (sut, repo, _) = Build();
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 80m));

        await sut.ApplyClaimFinalizedAsync(Claim("C1-VOID", 80m, frequency: "8", original: "C1"));

        Assert.Equal(0m, (await Snapshot(repo)).IndividualDeductibleUsed);
    }

    /// <summary>
    /// The audit row records what was actually applied after clamping at the
    /// limit, so the reversal backs out exactly that — not the requested
    /// amount (which would take another claim's deductible with it).
    /// </summary>
    [Fact]
    public async Task Reversal_BacksOutTheClampedAmount()
    {
        var (sut, repo, _) = Build(dedLimit: 500m);
        await sut.ApplyClaimFinalizedAsync(Claim("C1", 400m));
        await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m)); // only 100 fits under the limit
        Assert.Equal(500m, (await Snapshot(repo)).IndividualDeductibleUsed);

        await sut.ApplyClaimFinalizedAsync(Claim("C2", 300m, status: "Reversed"));

        Assert.Equal(400m, (await Snapshot(repo)).IndividualDeductibleUsed);
    }
}
