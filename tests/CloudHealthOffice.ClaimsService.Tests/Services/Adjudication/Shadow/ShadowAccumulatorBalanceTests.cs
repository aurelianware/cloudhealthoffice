using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.BenefitEngine.Domain;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowAccumulatorBalanceTests
{
    private readonly InMemoryShadowAccumulatorBalanceRepository _store = new();

    private ShadowAccumulatorBalanceImporter Importer(ICoreMemberCrosswalk? crosswalk = null)
        => new(_store, crosswalk ?? new PassThroughMemberCrosswalk());

    [Fact]
    public async Task Import_LoadsRows_AndReloadingReplaces()
    {
        const string file = "Member ID,As Of Date,Network,Individual Deductible Met,Individual OOP Met,Family Deductible Met,Family OOP Met\n" +
                            "M100,2026-06-30,in-network,400.00,650.00,900.00,1200.00\n" +
                            "M100,2026-06-30,out-of-network,100,100,,\n";

        var first = await Importer().ImportAsync("t1", file, "qnxt", dryRun: false, "user", CancellationToken.None);
        var again = await Importer().ImportAsync("t1", file, "qnxt", dryRun: false, "user", CancellationToken.None);

        Assert.Equal(2, first.Loaded);
        Assert.Equal(0, first.Rejected);
        Assert.Equal(2, again.Loaded);
        var stored = await _store.ListForMemberAsync("t1", "M100", 2026);
        Assert.Equal(2, stored.Count);
        var inNetwork = stored.Single(b => b.NetworkTier == "InNetwork");
        Assert.Equal(400m, inNetwork.IndividualDeductibleMet);
        Assert.Equal(1200m, inNetwork.FamilyOutOfPocketMet);
        Assert.Null(stored.Single(b => b.NetworkTier == "OutOfNetwork").FamilyDeductibleMet);
    }

    [Fact]
    public async Task Import_DryRun_WritesNothing_AndReportsBadRows()
    {
        const string file = "member_id|as_of_date|individual_deductible_met|family_deductible_met\n" +
                            "M100|2026-06-30|400|300\n" +       // family below individual
                            "M101|30 June|10|\n" +              // not a date
                            "M102|2026-06-30|-5|\n" +           // negative
                            "M103|2026-06-30|250|500\n";

        var result = await Importer().ImportAsync("t1", file, null, dryRun: true, "user", CancellationToken.None);

        Assert.Equal(4, result.RowsInFile);
        Assert.Equal(1, result.Loaded);
        Assert.Equal(3, result.Rejected);
        Assert.Contains(result.Errors, e => e.StartsWith("line 2:") && e.Contains("family_deductible_met is less"));
        Assert.Contains(result.Errors, e => e.StartsWith("line 3:") && e.Contains("not a date"));
        Assert.Contains(result.Errors, e => e.StartsWith("line 4:") && e.Contains("zero or more"));
        Assert.Empty(await _store.ListForMemberAsync("t1", "M103", 2026));
    }

    [Fact]
    public async Task Import_WithoutRequiredColumns_IsAFileError()
    {
        var result = await Importer().ImportAsync("t1", "member_id,network\nM1,in\n", null, false, "user", CancellationToken.None);

        Assert.Contains(result.FileErrors, e => e.Contains("as_of_date"));
        Assert.Contains(result.FileErrors, e => e.Contains("names no amount"));
    }

    [Fact]
    public async Task Import_UnknownMember_IsRefused()
    {
        var result = await Importer(new NobodyCrosswalk())
            .ImportAsync("t1", "member_id,as_of_date,individual_deductible_met\nX9,2026-06-30,10\n", "qnxt", false, "user", CancellationToken.None);

        Assert.Equal(1, result.Rejected);
        Assert.Contains("not in the member crosswalk", result.Errors.Single());
    }

    private sealed class NobodyCrosswalk : ICoreMemberCrosswalk
    {
        public Task<string?> ResolveAsync(string tenantId, string? sourceSystem, string sourceMemberId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ── history with a starting balance ──────────────────────────────────────

    private static readonly DateTime Aug10 = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime D(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static AdapterClaim Shadowed() => new()
    {
        Id = "C-NOW", MemberId = "M100", BenefitPlanId = "plan-1", ServiceDateFrom = Aug10, ServiceDateTo = Aug10,
    };

    private static Claim Core(string id, DateTime service, DateTime adjudicated, decimal deductible, string tier = "InNetwork") => new()
    {
        Id = id, MemberId = "M100", BenefitPlanId = "plan-1", ServiceDateFrom = service, ServiceDateTo = service,
        AdjudicatedDate = adjudicated, Status = ClaimStatus.Paid,
        AdjudicationResult = new AdjudicationResult { NetworkTier = tier, DeductibleAmount = deductible, PatientResponsibility = deductible },
    };

    private static ShadowAccumulatorBalance Balance(DateTime asOf, decimal ind, decimal? fam = null, string tier = "InNetwork") => new()
    {
        Id = $"b-{asOf:MMdd}-{tier}", TenantId = "t1", MemberId = "M100", AsOfDate = asOf, NetworkTier = tier,
        IndividualDeductibleMet = ind, IndividualOutOfPocketMet = ind, FamilyDeductibleMet = fam, FamilyOutOfPocketMet = fam,
    };

    [Fact]
    public void Balance_ReplacesTheClaimsItCovers_AndLaterClaimsAddToIt()
    {
        var self = Core("C-NOW", Aug10, D(8, 20), 0m);
        var coveredByBalance = Core("C-1", D(3, 1), D(3, 10), 300m);
        var afterBalance = Core("C-2", D(7, 2), D(7, 15), 50m);

        var prior = ShadowAccumulatorHistory.From(
            Shadowed(), [coveredByBalance, afterBalance, self], [Balance(D(6, 30), 400m, fam: 900m)]);

        Assert.Collection(prior.Entries,
            e => { Assert.True(e.ThisMember); Assert.Equal(400m, e.Deductible); Assert.Equal(new DateOnly(2026, 6, 30), e.ServiceDate); },
            e => { Assert.False(e.ThisMember); Assert.Equal(500m, e.Deductible); },
            e => { Assert.True(e.ThisMember); Assert.Equal(50m, e.Deductible); });
    }

    [Fact]
    public void Balance_UsesTheLatestBeforeTheClaim_PerNetwork()
    {
        var self = Core("C-NOW", Aug10, D(8, 20), 0m);
        var outOfNetworkClaim = Core("C-1", D(3, 1), D(3, 10), 70m, tier: "OutOfNetwork");

        var prior = ShadowAccumulatorHistory.From(Shadowed(), [outOfNetworkClaim, self],
        [
            Balance(D(5, 31), 200m),
            Balance(D(6, 30), 400m),
            Balance(D(9, 30), 999m), // after the core priced the claim
        ]);

        Assert.Equal(2, prior.Entries.Count);
        Assert.Equal(400m, prior.Entries[0].Deductible);
        Assert.Equal(NetworkTier.InNetwork, prior.Entries[0].NetworkTier);
        // No out-of-network balance: the out-of-network claim still counts.
        Assert.Equal(70m, prior.Entries[1].Deductible);
        Assert.Equal(NetworkTier.OutOfNetwork, prior.Entries[1].NetworkTier);
    }
}
