using PremiumBillingService.Rating;

namespace PremiumBillingService.Tests.Rating;

internal static class RatingFixtures
{
    public static readonly DateTime PlanYearStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime PlanYearEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    public static RateTable TierTable(string planId = "PPO-GOLD", TobaccoSurcharge? tobacco = null) => new()
    {
        Id = $"rt-{planId}-tier",
        PlanId = planId,
        EffectiveFrom = PlanYearStart,
        EffectiveTo = PlanYearEnd,
        Method = RatingMethod.Tier,
        TierRates = new TierAmounts { EmployeeOnly = 500m, EmployeeSpouse = 1000m, EmployeeChildren = 900m, Family = 1400m },
        Tobacco = tobacco
    };

    public static RateTable AgeBandTable(string planId = "HMO-SILVER", decimal baseRate = 300m, TobaccoSurcharge? tobacco = null) => new()
    {
        Id = $"rt-{planId}-age",
        PlanId = planId,
        EffectiveFrom = PlanYearStart,
        EffectiveTo = PlanYearEnd,
        Method = RatingMethod.AgeBand,
        AgeBandBaseRate = baseRate,
        Tobacco = tobacco
    };

    public static RatedMember Member(string id, MemberRelationship rel, DateTime dob, bool tobacco = false) => new()
    {
        MemberId = id,
        MemberName = id,
        Relationship = rel,
        DateOfBirth = dob,
        TobaccoUser = tobacco
    };

    public static RatingEnrollment Household(string coverageId, string planId, DateTime effective, DateTime? term,
        params RatedMember[] members) => new()
    {
        CoverageId = coverageId,
        PlanId = planId,
        EffectiveDate = effective,
        TerminationDate = term,
        InsuranceLineCode = "HLT",
        Members = members.ToList()
    };

    public static RatedMember Subscriber(string id, DateTime? dob = null, bool tobacco = false) =>
        Member(id, MemberRelationship.Subscriber, dob ?? D(1985, 5, 5), tobacco);
}
