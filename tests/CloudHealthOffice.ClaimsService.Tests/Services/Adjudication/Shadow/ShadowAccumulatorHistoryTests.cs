using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.BenefitEngine.Domain;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowAccumulatorHistoryTests
{
    private static readonly DateTime Apr15 = new(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

    private static AdapterClaim Shadowed() => new()
    {
        Id = "C-NOW", MemberId = "MEM-1", BenefitPlanId = "plan-1",
        ServiceDateFrom = Apr15, ServiceDateTo = Apr15,
    };

    private static Claim Core(string id, DateTime service, DateTime? adjudicated, decimal deductible, decimal liability,
        string member = "MEM-1", string? plan = "plan-1", string? tier = "InNetwork", decimal? oopApplied = null) => new()
    {
        Id = id, MemberId = member, BenefitPlanId = plan, ServiceDateFrom = service, ServiceDateTo = service,
        AdjudicatedDate = adjudicated, Status = ClaimStatus.Paid,
        AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = tier, DeductibleAmount = deductible, PatientResponsibility = liability, OopAppliedAmount = oopApplied,
        },
    };

    [Fact]
    public void EarlierClaims_CountByTheCoresAdjudicationOrder()
    {
        var self = Core("C-NOW", Apr15, new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc), 0m, 0m);
        var earlier = Core("C-1", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc), 300m, 350m);
        // Later service date, but the core processed it first: it counted.
        var laterServiceProcessedFirst = Core("C-2", new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc), 50m, 50m);
        // Earlier service date, but processed after: it did not.
        var processedAfter = Core("C-3", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 100m);

        var prior = ShadowAccumulatorHistory.From(Shadowed(), [earlier, laterServiceProcessedFirst, self, processedAfter]);

        Assert.Equal(new[] { 300m, 50m }, prior.Entries.Select(e => e.Deductible));
        Assert.Equal(new[] { 350m, 50m }, prior.Entries.Select(e => e.OutOfPocket));
        Assert.All(prior.Entries, e => Assert.True(e.ThisMember));
    }

    [Fact]
    public void WithoutAdjudicationDates_EarlierServiceCounts()
    {
        var before = Core("C-1", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), null, 100m, 100m);
        var sameDay = Core("C-2", Apr15, null, 100m, 100m);

        var prior = ShadowAccumulatorHistory.From(Shadowed(), [before, sameDay]);

        Assert.Equal("C-1 only", prior.Entries.Count == 1 && prior.Entries[0].ServiceDate == new DateOnly(2026, 2, 1) ? "C-1 only" : "other");
    }

    [Fact]
    public void Family_TierAndOopApplied_AreCarried_OtherPlansAndZeroClaimsAreNot()
    {
        var spouse = Core("C-1", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), null, 200m, 260m, member: "MEM-2");
        var outOfNetwork = Core("C-2", new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), null, 80m, 120m, tier: "OutOfNetwork", oopApplied: 90m);
        var otherPlan = Core("C-3", new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), null, 500m, 500m, plan: "plan-old");
        var nothingApplied = Core("C-4", new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), null, 0m, 0m);

        var prior = ShadowAccumulatorHistory.From(Shadowed(), [spouse, outOfNetwork, otherPlan, nothingApplied]);

        Assert.Equal(2, prior.Entries.Count);
        Assert.False(prior.Entries[0].ThisMember);
        Assert.Equal(NetworkTier.InNetwork, prior.Entries[0].NetworkTier);
        Assert.Equal(NetworkTier.OutOfNetwork, prior.Entries[1].NetworkTier);
        Assert.Equal(90m, prior.Entries[1].OutOfPocket);
    }

    [Fact]
    public async Task Build_AsksForTheMemberAndFamily_InTheClaimsYear()
    {
        var claims = Substitute.For<IClaimRepository>();
        claims.FindCoreClaimsForAccumulationAsync(default!, default, default, default, default, default)
            .ReturnsForAnyArgs(new List<Claim>());
        var context = new ClaimAdjudicationContext { TenantId = "t", ClaimVersionId = "C-NOW", Claim = Shadowed(), IsShadow = true };

        var prior = await new ShadowAccumulatorHistory(claims).BuildAsync(context, "SUB-1", CancellationToken.None);

        Assert.Empty(prior.Entries);
        await claims.Received(1).FindCoreClaimsForAccumulationAsync(
            "MEM-1", "SUB-1",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ShadowAccumulatorHistory.MaxClaims, Arg.Any<CancellationToken>());
    }
}
