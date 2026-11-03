using ClaimsService.Models;
using ClaimsService.Services.RetroRate;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.RetroRate;

public class RetroRateMatcherTests
{
    private const string ScheduleId = "t1:TMHP adult:20260901";
    private static readonly DateTime Dos = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    internal static FeeScheduleChange Replaced(params string[] codes) => new()
    {
        Id = ScheduleId + "@3",
        TenantId = "t1",
        FeeScheduleId = ScheduleId,
        FeeScheduleName = "TMHP adult",
        Kind = FeeScheduleChangeKind.Replaced,
        PreviousRevision = 2,
        NewRevision = 3,
        EffectiveDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        DefaultForPlanId = "STAR-TX",
        ChangedCodes = codes.ToList(),
    };

    internal static Claim PaidClaim(string plan = "STAR-TX", params (int Line, string Code)[] lines)
    {
        lines = lines.Length == 0 ? [(1, "99213"), (2, "36415")] : lines;
        return new Claim
        {
            Id = "doc-1",
            ClaimVersionId = "c-1",
            ClaimNumber = "CHO-1",
            TenantId = "t1",
            MemberId = "M-1",
            BillingProviderNPI = "1234567893",
            BenefitPlanId = plan,
            ClaimType = ClaimType.Professional,
            ServiceDateFrom = Dos,
            ServiceDateTo = Dos,
            Status = ClaimStatus.Paid,
            ClaimLines = lines.Select(l => new ClaimLine { LineNumber = l.Line, ProcedureCode = l.Code, ChargeAmount = 100m, ServiceDateFrom = Dos }).ToList(),
            AdjudicationResult = new AdjudicationResult { AllowedAmount = 40m, PayerPayment = 40m },
        };
    }

    private static ClaimLinePricing Priced(int line, string code, string scheduleId = ScheduleId, long? revision = 2) => new()
    {
        LineNumber = line, ProcedureCode = code, ServiceDate = Dos, FeeScheduleId = scheduleId, FeeScheduleRevision = revision,
    };

    [Fact]
    public void LinePricedOnAnOlderRevision_OfAChangedCode_Matches()
    {
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "99213"), Priced(2, "36415")];

        var match = RetroRateMatcher.Match(claim, Replaced("99213"));

        Assert.NotNull(match);
        Assert.Equal(RetroRateMatch.Provenance, match!.Match);
        Assert.Equal([1], match.AffectedLines);
        Assert.Equal(2, match.PricedOnRevision);
    }

    [Fact]
    public void LinePricedOnTheCurrentRevision_DoesNotMatch()
    {
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "99213", revision: 3)];

        Assert.Null(RetroRateMatcher.Match(claim, Replaced("99213")));
    }

    [Fact]
    public void UnchangedCodes_DoNotMatch_UnlessAllCodesChanged()
    {
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "99213"), Priced(2, "36415")];

        Assert.Null(RetroRateMatcher.Match(claim, Replaced("99214")));

        var all = Replaced();
        all.AllCodes = true;
        Assert.Equal([1, 2], RetroRateMatcher.Match(claim, all)!.AffectedLines);
    }

    [Fact]
    public void PricedOnAnotherSchedule_DoesNotMatch_EvenOnThePlan()
    {
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "99213", scheduleId: "t1:contract:abc")];

        Assert.Null(RetroRateMatcher.Match(claim, Replaced("99213")));
    }

    [Fact]
    public void NoProvenance_FallsBackToPlanDateAndCode()
    {
        var match = RetroRateMatcher.Match(PaidClaim(), Replaced("36415"));

        Assert.Equal(RetroRateMatch.PlanFallback, match!.Match);
        Assert.Equal([2], match.AffectedLines);
        Assert.Null(RetroRateMatcher.Match(PaidClaim(plan: "OTHER"), Replaced("36415")));
    }

    [Fact]
    public void ServiceBeforeTheEffectiveDate_DoesNotMatch()
    {
        var change = Replaced("99213");
        change.EffectiveDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(RetroRateMatcher.Match(PaidClaim(), change));
    }

    [Fact]
    public void SupplementalPaymentFromTheSchedule_Matches()
    {
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "D1110", scheduleId: "t1:medi-cal")];
        claim.AdjudicationResult.SupplementalPayments = [new ClaimSupplementalPayment { LineNumber = 1, ProcedureCode = "D1110", FeeScheduleId = ScheduleId }];

        var match = RetroRateMatcher.Match(claim, Replaced("D1110"));

        Assert.Equal(RetroRateMatch.Supplemental, match!.Match);
    }

    [Fact]
    public void NewRetroactiveSchedule_MatchesClaimsOnItsPlanPricedElsewhere()
    {
        var change = Replaced();
        change.Kind = FeeScheduleChangeKind.Created;
        change.PreviousRevision = null;
        change.NewRevision = 1;
        var claim = PaidClaim();
        claim.AdjudicationResult!.LinePricing = [Priced(1, "99213", scheduleId: "t1:TMHP adult:20250901")];

        var match = RetroRateMatcher.Match(claim, change);

        Assert.Equal(RetroRateMatch.NewSchedule, match!.Match);
        Assert.Equal([1, 2], match.AffectedLines);
    }

    [Fact]
    public void ServiceWindow_CoversAMovedEffectiveDate()
    {
        var change = Replaced();
        change.PreviousEffectiveDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        change.EffectiveDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        change.PreviousTermDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        change.TermDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        var (from, before) = RetroRateMatcher.ServiceWindow(change);

        Assert.Equal(new DateTime(2026, 7, 1), from.Date);
        Assert.Equal(new DateTime(2027, 1, 1), before!.Value.Date);
    }

    [Theory]
    [InlineData(true, 5.00, "Underpaid")]
    [InlineData(true, -5.00, "Overpaid")]
    [InlineData(true, 0.01, "Unchanged")]
    [InlineData(false, 5.00, "Undecided")]
    public void Direction_FollowsThePaidDifference(bool decided, double delta, string expected) =>
        Assert.Equal(expected, RetroRateMatcher.Direction(decided, (decimal)delta, 0.01m));
}
