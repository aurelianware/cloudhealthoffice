using ClaimsService.EDI.Validation;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// WEDI SNIP 1–5 validation of synthetic 837P / 837I files. Each malformed
/// case changes one thing in an otherwise clean file and asserts the level,
/// rule, position and outcome.
/// </summary>
public class X12837SnipValidatorTests
{
    private static SnipValidationResult Validate(string edi, Snip837ValidationOptions? options = null, ISnipCodeSetReference? codeSets = null)
        => new X12837SnipValidator(options, codeSets).Validate(edi);

    private static SnipIssue Single(SnipValidationResult result, string ruleId)
        => Assert.Single(result.AllIssues, i => i.RuleId == ruleId);

    // ── Clean files ──────────────────────────────────────────────────

    [Fact]
    public void CleanProfessional_IsAcceptedWithNoIssues()
    {
        var result = Validate(Professional());
        Assert.Empty(result.AllIssues);
        Assert.Equal("A", result.AcknowledgmentCode);
        Assert.Single(result.AcceptedTransactionSets);
    }

    [Fact]
    public void CleanInstitutional_IsAcceptedWithNoIssues()
    {
        var result = Validate(Institutional());
        Assert.Empty(result.AllIssues);
        Assert.Equal("A", result.AcknowledgmentCode);
    }

    // ── Level 1: syntax / envelope ───────────────────────────────────

    [Fact]
    public void L1_NotX12_IsUnreadable()
    {
        var result = Validate("NOT AN 837 FILE");
        Assert.Null(result.Document);
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Equal(SnipLevel.Syntax, Single(result, "L1-ISA-UNREADABLE").Level);
    }

    [Fact]
    public void L1_SeCountWrong_RejectsTransactionSet()
    {
        var edi = Professional().Replace("SE*24*0001", "SE*17*0001");
        var result = Validate(edi);
        var issue = Single(result, "L1-SE01");
        Assert.Equal("SE", issue.SegmentId);
        Assert.Equal(24, issue.SegmentPosition);
        Assert.Contains("4", result.TransactionSets.Single().TransactionSetErrorCodes);
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    [Fact]
    public void L1_IeaControlMismatch_RejectsInterchange()
    {
        var result = Validate(Professional().Replace("IEA*1*000000101", "IEA*1*000000999"));
        Assert.Equal(SnipSeverity.Error, Single(result, "L1-IEA-CONTROL").Severity);
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Empty(result.AcceptedTransactionSets);
    }

    [Fact]
    public void L1_GeCountMismatch_RejectsGroupWithAk905Code5()
    {
        var result = Validate(Professional().Replace("GE*1*101", "GE*2*101"));
        Single(result, "L1-GE-COUNT");
        Assert.Contains("5", result.FunctionalGroups.Single().GroupErrorCodes);
        Assert.Equal("R", result.FunctionalGroups.Single().AcknowledgmentCode);
    }

    [Fact]
    public void L1_NonNumericCharge_IsInvalidCharacter()
    {
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*15O.00***11:B:1*Y*A*Y*Y")));
        var issue = result.AllIssues.First(i => i.RuleId == "L1-NUMERIC");
        Assert.Equal(("CLM", 2, "6"), (issue.SegmentId, issue.ElementPosition, issue.ElementErrorCode));
    }

    [Fact]
    public void L1_InvalidServiceDate_IsInvalidDate()
    {
        var result = Validate(Professional(b => b.Replace("DTP*472", "DTP*472*D8*20260230")));
        var issue = Single(result, "L1-DATE");
        Assert.Equal(("DTP", 3, "8", "2400"), (issue.SegmentId, issue.ElementPosition, issue.ElementErrorCode, issue.Loop));
    }

    [Fact]
    public void L1_UnknownSegment_IsUnrecognizedSegmentId()
    {
        var result = Validate(Professional(b => b.Insert(16, "ZZZ*1")));
        var issue = Single(result, "L1-SEGMENT-ID");
        Assert.Equal("1", issue.SegmentErrorCode);
        Assert.Equal(18, issue.SegmentPosition);
    }

    [Fact]
    public void L1_DuplicateStControlNumber_RejectsSecondSet()
    {
        var edi = Wrap(ProfessionalVersion, ProfessionalBody(), ProfessionalBody("PCN0002"))
            .Replace("ST*837*0002", "ST*837*0001").Replace("SE*24*0002", "SE*24*0001");
        var result = Validate(edi);
        Single(result, "L1-ST02-DUPLICATE");
        Assert.Equal(["A", "R"], result.TransactionSets.Select(t => t.AcknowledgmentCode));
        Assert.Equal("P", result.AcknowledgmentCode);
    }

    // ── Level 2: implementation guide ────────────────────────────────

    [Fact]
    public void L2_MissingBillingProviderTaxId()
    {
        var issue = Single(Validate(Professional(b => b.RemoveSegment("REF*EI"))), "L2-2010AA-TAXID");
        Assert.Equal(("REF", "2010AA", "3"), (issue.SegmentId, issue.Loop, issue.SegmentErrorCode));
    }

    [Fact]
    public void L2_WrongBht01()
    {
        var issue = Single(Validate(Professional(b => b.Replace("BHT*", "BHT*0022*00*BATCH0001*20260115*1200*CH"))), "L2-BHT01");
        Assert.Equal(("BHT", 1, "0022"), (issue.SegmentId, issue.ElementPosition, issue.BadValue));
    }

    [Fact]
    public void L2_ProfessionalClaimWithInstitutionalQualifier()
    {
        var issue = Single(Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:A:1*Y*A*Y*Y"))), "L2-CLM05-2");
        Assert.Equal((5, 2, "PCN0001"), (issue.ElementPosition, issue.ComponentPosition, issue.ClaimId));
    }

    [Fact]
    public void L2_PrincipalDiagnosisMissing()
    {
        Single(Validate(Professional(b => b.Replace("HI*", "HI*ABF:J069*ABF:R05"))), "L2-HI-PRINCIPAL");
    }

    [Fact]
    public void L2_SubscriberHlPointsToWrongParent()
    {
        var issue = Single(Validate(Professional(b => b.Replace("HL*2", "HL*2*7*22*0"))), "L2-HL02");
        Assert.Equal(("HL", "2000B"), (issue.SegmentId, issue.Loop));
    }

    [Fact]
    public void L2_LineWithoutSv1()
    {
        Single(Validate(Professional(b => b.RemoveSegment("SV1*HC:87880"))), "L2-SV1-MISSING");
    }

    [Fact]
    public void L2_LxOutOfSequence()
    {
        Single(Validate(Professional(b => b.Replace("LX*2", "LX*3"))), "L2-LX01");
    }

    [Fact]
    public void L2_InstitutionalWithoutStatementDates()
    {
        Single(Validate(Institutional(b => b.RemoveSegment("DTP*434"))), "L2-DTP434");
    }

    // ── Level 3: balancing ───────────────────────────────────────────

    [Fact]
    public void L3_ProfessionalChargeDoesNotBalance()
    {
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")));
        var issue = Single(result, "L3-CLM-BALANCE");
        Assert.Equal((SnipLevel.Balancing, "CLM", 16, "2300", 2, "I12"),
            (issue.Level, issue.SegmentId, issue.SegmentPosition, issue.Loop, issue.ElementPosition, issue.ElementErrorCode));
        Assert.Contains("175.00", issue.Message);
        Assert.Contains("150.00", issue.Message);
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    [Fact]
    public void L3_InstitutionalChargeDoesNotBalance()
    {
        Single(Validate(Institutional(b => b.Replace("SV2*0300", "SV2*0300*HC:80053*90.00*UN*1"))), "L3-CLM-BALANCE");
    }

    [Fact]
    public void L3_ConfiguredToWarn_AcceptsWithErrors()
    {
        var result = Validate(
            Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")),
            new Snip837ValidationOptions { Level3 = SnipAction.Warn });
        Assert.Equal(SnipSeverity.Warning, Single(result, "L3-CLM-BALANCE").Severity);
        Assert.Equal("E", result.AcknowledgmentCode);
        Assert.Single(result.AcceptedTransactionSets);
    }

    [Fact]
    public void L3_ConfiguredOff_SkipsTheCheck()
    {
        var result = Validate(
            Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")),
            new Snip837ValidationOptions { Level3 = SnipAction.Off });
        Assert.Empty(result.AllIssues);
    }

    // ── Level 4: situational ─────────────────────────────────────────

    [Fact]
    public void L4_ProfessionalLineWithoutServiceDate()
    {
        var issue = Single(Validate(Professional(b => b.RemoveAt(b.FindLastIndex(s => s.StartsWith("DTP*472"))))), "L4-DTP472");
        Assert.Equal(("DTP", "2400", "I6"), (issue.SegmentId, issue.Loop, issue.SegmentErrorCode));
    }

    [Fact]
    public void L4_NpiFailsCheckDigit()
    {
        var issue = Single(Validate(Professional(b => b.Replace("NM1*85", "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567890"))), "L4-NPI-CHECKDIGIT");
        Assert.Equal(("NM1", 9, "2010AA", "1234567890"), (issue.SegmentId, issue.ElementPosition, issue.Loop, issue.BadValue));
    }

    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1003000126", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678AB", false)]
    public void L4_NpiLuhn(string npi, bool valid) => Assert.Equal(valid, SnipCodeSets.IsValidNpi(npi));

    [Fact]
    public void L4_BillingProviderPoBox()
    {
        Single(Validate(Professional(b => b.Replace("N3*100", "N3*P.O. BOX 123"))), "L4-2010AA-POBOX");
    }

    [Fact]
    public void L4_ReplacementClaimWithoutPayerClaimNumber()
    {
        Single(Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:B:7*Y*A*Y*Y"))), "L4-REF-F8");
        Assert.Empty(Validate(Professional(b =>
        {
            b.Replace("CLM*", "CLM*PCN0001*150.00***11:B:7*Y*A*Y*Y");
            b.Insert(b.FindIndex(s => s.StartsWith("HI*")), "REF*F8*ORIGCLAIM01");
        })).AllIssues);
    }

    [Fact]
    public void L4_DiagnosisPointerBeyondDiagnoses()
    {
        var issue = Single(Validate(Professional(b => b.Replace("SV1*HC:87880", "SV1*HC:87880*50.00*UN*1***3"))), "L4-SV107-POINTER");
        Assert.Equal((7, 1, "3"), (issue.ElementPosition, issue.ComponentPosition, issue.BadValue));
    }

    [Fact]
    public void L4_SubscriberIsPatientWithoutDemographics()
    {
        Single(Validate(Professional(b => b.RemoveSegment("DMG*"))), "L4-2010BA-DMG");
    }

    [Fact]
    public void L4_OutpatientMultiDayStatementNeedsLineDates()
    {
        Single(Validate(Institutional(b => b.RemoveSegment("DTP*472*D8*20260106"))), "L4-DTP472");
    }

    [Fact]
    public void L4_InpatientNeedsAdmissionDate()
    {
        Single(Validate(Institutional(b => b.Replace("CLM*", "CLM*PCN0002*300.00***11:A:1**A*Y*Y"))), "L4-DTP435");
    }

    [Fact]
    public void L4_LineDateOutsideStatementPeriod()
    {
        Single(Validate(Institutional(b => b.Replace("DTP*472*D8*20260106", "DTP*472*D8*20260109"))), "L4-DTP472-PERIOD");
    }

    // ── Level 5: external code sets (default: warn) ──────────────────

    [Fact]
    public void L5_IcdWithDecimalPoint_WarnsByDefault()
    {
        var result = Validate(Professional(b => b.Replace("HI*", "HI*ABK:J06.9*ABF:R05")));
        var issue = Single(result, "L5-ICD10CM-FORMAT");
        Assert.Equal((SnipSeverity.Warning, "J06.9", 1, 2), (issue.Severity, issue.BadValue, issue.ElementPosition, issue.ComponentPosition));
        Assert.Equal("E", result.AcknowledgmentCode);
    }

    [Fact]
    public void L5_Icd9QualifierAfterCutover()
    {
        Single(Validate(Professional(b => b.Replace("HI*", "HI*BK:4659"))), "L5-ICD9");
    }

    [Fact]
    public void L5_BadProcedureCodeAndModifier()
    {
        var result = Validate(Professional(b => b.Replace("SV1*HC:99213", "SV1*HC:9921:ABC*100.00*UN*1***1:2")));
        Single(result, "L5-CPT-FORMAT");
        Single(result, "L5-MODIFIER");
    }

    [Fact]
    public void L5_UnknownPlaceOfService()
    {
        var issue = Single(Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***98:B:1*Y*A*Y*Y"))), "L5-POS");
        Assert.Equal("98", issue.BadValue);
    }

    [Fact]
    public void L5_BadRevenueCode()
    {
        Single(Validate(Institutional(b => b.Replace("SV2*0450", "SV2*45*HC:99283*200.00*UN*1"))), "L5-REV-FORMAT");
    }

    [Fact]
    public void L5_CodeSetReference_RejectsUnknownCodeWhenConfiguredToReject()
    {
        var reference = new StubCodeSets(("ICD10CM", "R05"));
        var result = Validate(Professional(), new Snip837ValidationOptions { Level5 = SnipAction.Reject }, reference);
        var issue = Single(result, "L5-ICD10CM-UNKNOWN");
        Assert.Equal(("R05", SnipSeverity.Error), (issue.BadValue, issue.Severity));
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    private sealed class StubCodeSets(params (string System, string Code)[] unknown) : ISnipCodeSetReference
    {
        public bool? IsValid(string codeSystem, string code, DateOnly? serviceDate) =>
            unknown.Contains((codeSystem, code)) ? false : null;
    }
}
