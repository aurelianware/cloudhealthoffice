using ClaimsService.EDI.Validation;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// WEDI SNIP 1–5 validation of a synthetic 837D (005010X224A2). The clean
/// file is <see cref="X12837DentalTests.Dental"/>; each malformed case
/// changes one thing in it.
/// </summary>
public class SnipDentalTests
{
    private static SnipValidationResult Validate(string edi) => new X12837SnipValidator().Validate(edi);

    private static SnipIssue Single(SnipValidationResult result, string ruleId)
        => Assert.Single(result.AllIssues, i => i.RuleId == ruleId);

    [Fact]
    public void CleanDental_IsAcceptedWithNoIssues()
    {
        var result = Validate(X12837DentalTests.Dental);
        Assert.Empty(result.AllIssues);
        Assert.Equal("A", result.AcknowledgmentCode);
    }

    [Fact]
    public void Dental_WithoutDiagnoses_IsAccepted()
    {
        var edi = X12837DentalTests.Dental
            .Replace("HI*ABK:K0262~", string.Empty)
            .Replace("SV3*AD:D2740*1200.00*11**R*1*****1~", "SV3*AD:D2740*1200.00*11**R*1~")
            .Replace("SE*31*0301", "SE*30*0301");
        var result = Validate(edi);
        Assert.Empty(result.AllIssues);
    }

    [Fact]
    public void L2_Sv301WithoutAdQualifier_IsReported()
    {
        var edi = X12837DentalTests.Dental.Replace("SV3*AD:D4355", "SV3*HC:D4355");
        Assert.Equal(SnipLevel.ImplementationGuide, Single(Validate(edi), "L2-SV301").Level);
    }

    [Fact]
    public void L2_Sv305MustBeInitialOrReplacement()
    {
        var edi = X12837DentalTests.Dental.Replace("*11**R*1*", "*11**X*1*");
        Single(Validate(edi), "L2-SV305");
    }

    [Fact]
    public void L2_TooWithoutJpQualifier_IsReported()
    {
        var edi = X12837DentalTests.Dental.Replace("TOO*JP*19~", "TOO*XX*19~");
        Single(Validate(edi), "L2-TOO01");
    }

    [Fact]
    public void L3_ClaimTotalMustEqualSumOfSv302()
    {
        var edi = X12837DentalTests.Dental.Replace("CLM*DEN-0001*1585.00", "CLM*DEN-0001*1600.00");
        var issue = Single(Validate(edi), "L3-CLM-BALANCE");
        Assert.Contains("SV302", issue.Message);
    }

    [Fact]
    public void L4_Sv311PointerMustPointToADiagnosis()
    {
        var edi = X12837DentalTests.Dental.Replace("*R*1*****1~", "*R*1*****3~");
        Single(Validate(edi), "L4-SV311-POINTER");
    }

    [Fact]
    public void L5_ProcedureMustBeCdtFormat()
    {
        var edi = X12837DentalTests.Dental.Replace("AD:D2392", "AD:02392");
        Single(Validate(edi), "L5-CDT");
    }

    [Fact]
    public void L4_DentalLineWithoutServiceDate_IsRejected()
    {
        var edi = X12837DentalTests.Dental
            .Replace("SV3*AD:D4355*125.00**01~DTP*472*D8*20260226~", "SV3*AD:D4355*125.00**01~")
            .Replace("SE*31*0301", "SE*30*0301");
        var result = Validate(edi);
        Single(result, "L4-DTP472");
        Assert.Equal("R", result.AcknowledgmentCode);
    }
}
