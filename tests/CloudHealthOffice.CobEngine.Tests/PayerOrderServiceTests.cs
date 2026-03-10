using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Xunit;

namespace CloudHealthOffice.CobEngine.Tests;

public class PayerOrderServiceTests
{
    private static PayerOrderService Make() => new();

    // ── Single coverage ────────────────────────────────────────────────────

    [Fact]
    public void SingleCoverage_AlwaysPrimary()
    {
        var svc = Make();
        var coverage = new InsuredInfo { MemberId = "M1", PayerId = "PAYER1" };

        var result = svc.DetermineOrder(coverage, [coverage]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.ExplicitCoverageRecord, result.Rule);
    }

    // ── Medicare Secondary Payer (MSP) rules ───────────────────────────────

    [Fact]
    public void Medicare_WithActiveLghpOtherCoverage_IsSecondary()
    {
        var svc = Make();
        var medicare = new InsuredInfo { MemberId = "M1", PayerId = "MEDICARE", IsMedicare = true };
        var employer  = new InsuredInfo { MemberId = "M1", PayerId = "EMPLOYER", IsActiveEmployee = true, IsLargeGroupHealthPlan = true };

        var result = svc.DetermineOrder(medicare, [medicare, employer]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.MedicareSecondaryPayer, result.Rule);
    }

    [Fact]
    public void Medicare_NoActiveLghp_IsPrimary()
    {
        var svc = Make();
        var medicare = new InsuredInfo { MemberId = "M1", PayerId = "MEDICARE", IsMedicare = true };
        var retiree  = new InsuredInfo { MemberId = "M1", PayerId = "RETIREE", IsActiveEmployee = false };

        var result = svc.DetermineOrder(medicare, [medicare, retiree]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.MedicarePrimary, result.Rule);
    }

    [Fact]
    public void Medicare_DesignatedPrimary_IsPrimary()
    {
        var svc = Make();
        var medicare = new InsuredInfo
        {
            MemberId = "M1", PayerId = "MEDICARE",
            IsMedicare = true,
            MedicareDesignatedPrimary = true
        };
        var employer = new InsuredInfo { MemberId = "M1", PayerId = "EMPLOYER", IsActiveEmployee = true, IsLargeGroupHealthPlan = true };

        var result = svc.DetermineOrder(medicare, [medicare, employer]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.ExplicitCoverageRecord, result.Rule);
    }

    [Fact]
    public void NonMedicare_OtherCoverageIsMedicareDesignatedPrimary_IsSecondary()
    {
        var svc = Make();
        var commercial = new InsuredInfo { MemberId = "M1", PayerId = "COMMERCIAL" };
        var medicare   = new InsuredInfo { MemberId = "M1", PayerId = "MEDICARE", IsMedicare = true, MedicareDesignatedPrimary = true };

        var result = svc.DetermineOrder(commercial, [commercial, medicare]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
    }

    // ── Active employment rule ─────────────────────────────────────────────

    [Fact]
    public void ActiveEmployee_OtherIsNotActive_IsPrimary()
    {
        var svc = Make();
        var active = new InsuredInfo { MemberId = "M1", PayerId = "P1", IsActiveEmployee = true };
        var cobra  = new InsuredInfo { MemberId = "M2", PayerId = "P2", IsActiveEmployee = false };

        var result = svc.DetermineOrder(active, [active, cobra]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.ActiveEmployment, result.Rule);
    }

    [Fact]
    public void NotActiveEmployee_OtherIsActive_IsSecondary()
    {
        var svc = Make();
        var cobra  = new InsuredInfo { MemberId = "M1", PayerId = "P1", IsActiveEmployee = false };
        var active = new InsuredInfo { MemberId = "M2", PayerId = "P2", IsActiveEmployee = true };

        var result = svc.DetermineOrder(cobra, [cobra, active]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.ActiveEmployment, result.Rule);
    }

    // ── Birthday rule ──────────────────────────────────────────────────────

    [Fact]
    public void BirthdayRule_EarlierBirthday_IsPrimary()
    {
        var svc = Make();
        // Jan 15 vs Mar 20 — Jan is earlier
        var jan = new InsuredInfo
        {
            MemberId = "M1", PayerId = "P1", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1980, 1, 15)
        };
        var mar = new InsuredInfo
        {
            MemberId = "M2", PayerId = "P2", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1975, 3, 20)
        };

        var result = svc.DetermineOrder(jan, [jan, mar]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.BirthdayRule, result.Rule);
    }

    [Fact]
    public void BirthdayRule_LaterBirthday_IsSecondary()
    {
        var svc = Make();
        // Sep 10 vs Feb 5 — Feb is earlier
        var sep = new InsuredInfo
        {
            MemberId = "M1", PayerId = "P1", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1982, 9, 10)
        };
        var feb = new InsuredInfo
        {
            MemberId = "M2", PayerId = "P2", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1985, 2, 5)
        };

        var result = svc.DetermineOrder(sep, [sep, feb]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.BirthdayRule, result.Rule);
    }

    [Fact]
    public void BirthdayRule_SameBirthday_LongerDurationWins()
    {
        var svc = Make();
        // Both born June 15 — one has earlier effective date → primary
        var longer = new InsuredInfo
        {
            MemberId = "M1", PayerId = "P1", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1980, 6, 15),
            CoverageEffectiveDate  = new DateOnly(2015, 1, 1)  // earlier → longer duration
        };
        var shorter = new InsuredInfo
        {
            MemberId = "M2", PayerId = "P2", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1982, 6, 15),
            CoverageEffectiveDate  = new DateOnly(2020, 1, 1)
        };

        var result = svc.DetermineOrder(longer, [longer, shorter]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.LongerDuration, result.Rule);
    }

    [Fact]
    public void BirthdayRule_SameBirthday_ShorterDurationIsSecondary()
    {
        var svc = Make();
        var longer = new InsuredInfo
        {
            MemberId = "M1", PayerId = "P1", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1980, 6, 15),
            CoverageEffectiveDate  = new DateOnly(2015, 1, 1)
        };
        var shorter = new InsuredInfo
        {
            MemberId = "M2", PayerId = "P2", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1982, 6, 15),
            CoverageEffectiveDate  = new DateOnly(2020, 1, 1)
        };

        var result = svc.DetermineOrder(shorter, [longer, shorter]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.LongerDuration, result.Rule);
    }

    [Fact]
    public void BirthdayRule_SameBirthday_NoDuration_DefaultsPrimary()
    {
        var svc = Make();
        // Same birthday, no effective dates — cannot break tie → default primary
        var a = new InsuredInfo
        {
            MemberId = "M1", PayerId = "P1", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1980, 6, 15)
        };
        var b = new InsuredInfo
        {
            MemberId = "M2", PayerId = "P2", IsActiveEmployee = true,
            PolicyholderBirthDate = new DateOnly(1982, 6, 15)
        };

        var result = svc.DetermineOrder(a, [a, b]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.BirthdayRule, result.Rule);
    }

    // ── Rule priority: MSP over active-employment ──────────────────────────

    [Fact]
    public void MspRule_TakesPrecedenceOverBirthdayRule()
    {
        var svc = Make();
        // Medicare member has earlier birthday, but MSP makes them secondary
        var medicare = new InsuredInfo
        {
            MemberId = "M1", PayerId = "MEDICARE", IsMedicare = true,
            PolicyholderBirthDate = new DateOnly(1955, 1, 1)
        };
        var employer = new InsuredInfo
        {
            MemberId = "M1", PayerId = "EMPLOYER",
            IsActiveEmployee = true, IsLargeGroupHealthPlan = true,
            PolicyholderBirthDate = new DateOnly(1955, 12, 31)
        };

        var result = svc.DetermineOrder(medicare, [medicare, employer]);

        Assert.Equal(PayerSequenceCode.Secondary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.MedicareSecondaryPayer, result.Rule);
    }

    // ── Medicare with small employer (< 20) ────────────────────────────────

    [Fact]
    public void Medicare_WithActiveSmallGroupEmployer_IsPrimary()
    {
        var svc = Make();
        // LGHP = false → small employer → Medicare is primary (no MSP exception)
        var medicare = new InsuredInfo { MemberId = "M1", PayerId = "MEDICARE", IsMedicare = true };
        var small    = new InsuredInfo { MemberId = "M1", PayerId = "SMALL_EMP", IsActiveEmployee = true, IsLargeGroupHealthPlan = false };

        var result = svc.DetermineOrder(medicare, [medicare, small]);

        Assert.Equal(PayerSequenceCode.Primary, result.PayerSequence);
        Assert.Equal(PayerOrderRule.MedicarePrimary, result.Rule);
    }
}
