using ClaimsService.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Models;

public class InstitutionalClaimDetailsTests
{
    [Theory]
    [InlineData("11", "1", "111")]
    [InlineData("13", "1", "131")]
    [InlineData("11", "7", "117")]
    [InlineData(null, "1", null)]
    [InlineData("1", "1", null)]
    [InlineData("11", null, null)]
    [InlineData("11", "", null)]
    public void ComposeTypeOfBill_FacilityTypePlusFrequency(string? facility, string? frequency, string? expected)
    {
        Assert.Equal(expected, InstitutionalClaimDetails.ComposeTypeOfBill(facility, frequency));
    }

    [Fact]
    public void Claim_TypeOfBill_TracksFrequencyCode()
    {
        var claim = new Claim
        {
            ClaimType = ClaimType.Institutional,
            ClaimFrequencyCode = "1",
            Institutional = new InstitutionalClaimDetails { FacilityTypeCode = "11" },
        };
        Assert.Equal("111", claim.TypeOfBill);

        claim.ClaimFrequencyCode = "7"; // replacement
        Assert.Equal("117", claim.TypeOfBill);

        Assert.Null(new Claim().TypeOfBill); // professional: no facility type
    }

    [Fact]
    public void LengthOfStay_AdmissionToStatementThrough_DischargeDayNotCounted()
    {
        var details = new InstitutionalClaimDetails
        {
            AdmissionDate = new DateTime(2026, 2, 10),
            StatementFromDate = new DateTime(2026, 2, 10),
            StatementToDate = new DateTime(2026, 2, 14),
            PatientStatusCode = "01",
        };

        Assert.Equal(4, details.CalculateLengthOfStay());
    }

    [Fact]
    public void LengthOfStay_InterimContinuingBill_CountsOnlyThisBillsStatementPeriod()
    {
        // Admitted Jan 1; this bill covers Feb 1–Feb 10 with the patient
        // discharged on the 10th → 9 days, not the 40 days since admission.
        var details = new InstitutionalClaimDetails
        {
            AdmissionDate = new DateTime(2026, 1, 1),
            StatementFromDate = new DateTime(2026, 2, 1),
            StatementToDate = new DateTime(2026, 2, 10),
            PatientStatusCode = "01",
        };

        Assert.Equal(9, details.CalculateLengthOfStay());
    }

    [Fact]
    public void LengthOfStay_StillAPatient_CountsThroughDate()
    {
        var details = new InstitutionalClaimDetails
        {
            AdmissionDate = new DateTime(2026, 2, 10),
            StatementFromDate = new DateTime(2026, 2, 10),
            StatementToDate = new DateTime(2026, 2, 14),
            PatientStatusCode = "30",
        };

        Assert.Equal(5, details.CalculateLengthOfStay());
    }

    [Fact]
    public void LengthOfStay_ExplicitDischargeDateWinsOverStatementThrough()
    {
        var details = new InstitutionalClaimDetails
        {
            AdmissionDate = new DateTime(2026, 2, 10, 8, 15, 0),
            DischargeDate = new DateTime(2026, 2, 12),
            StatementToDate = new DateTime(2026, 2, 14),
        };

        Assert.Equal(2, details.CalculateLengthOfStay());
    }

    [Fact]
    public void LengthOfStay_NoAdmissionDate_UsesStatementPeriod_SameDayIsOne()
    {
        var details = new InstitutionalClaimDetails
        {
            StatementFromDate = new DateTime(2026, 3, 5),
            StatementToDate = new DateTime(2026, 3, 5),
        };

        Assert.Equal(1, details.CalculateLengthOfStay());
    }

    [Fact]
    public void LengthOfStay_MissingOrInvertedDates_IsNull()
    {
        Assert.Null(new InstitutionalClaimDetails().CalculateLengthOfStay());
        Assert.Null(new InstitutionalClaimDetails { AdmissionDate = new DateTime(2026, 2, 10) }.CalculateLengthOfStay());
        Assert.Null(new InstitutionalClaimDetails
        {
            AdmissionDate = new DateTime(2026, 2, 14),
            StatementToDate = new DateTime(2026, 2, 10),
        }.CalculateLengthOfStay());
    }
}
