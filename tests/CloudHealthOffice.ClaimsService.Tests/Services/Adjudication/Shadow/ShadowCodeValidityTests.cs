using ClaimsService.Services.Adjudication.CodeValidity;
using ClaimsService.Services.Adjudication.Shadow;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

/// <summary>
/// Codes the core paid that were not valid on the date of service: a lead in the
/// shadow CSV and report even when both sides paid the same amounts.
/// </summary>
public class ShadowCodeValidityTests
{
    private static CodeValidityFinding Finding(string code, string reason, int? line = null) => new()
    {
        Kind = line is null ? CodeValidityFinding.DiagnosisKind : CodeValidityFinding.ProcedureKind,
        LineNumber = line,
        CodeSystem = line is null ? "ICD-10-CM" : "HCPCS",
        Code = code,
        Reason = reason,
        DateOfService = new DateTime(2026, 3, 1),
    };

    private static ShadowAdjudicationResult Result(string claim, string kind, string coreOutcome, params CodeValidityFinding[] findings) => new()
    {
        Id = "r-" + claim,
        ClaimNumber = claim,
        Kind = kind,
        CoreOutcome = coreOutcome,
        ChoOutcome = ShadowOutcome.Paid,
        CorePaid = 60m,
        ChoPaid = 60m,
        CodeValidityFindings = [.. findings],
    };

    [Fact]
    public void CorePaid_withAnInvalidCode_IsAFinding_DeniedIsNot()
    {
        Assert.Equal(ShadowCodeValidity.CorePaidInvalidCode,
            ShadowCodeValidity.For(Result("C1", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid, Finding("E11", "not-billable"))));
        Assert.Null(ShadowCodeValidity.For(Result("C2", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, Finding("E11", "not-billable"))));
        Assert.Null(ShadowCodeValidity.For(Result("C3", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid)));
    }

    [Fact]
    public void TheCsv_KeepsAMatchThatCarriesAnInvalidCode_AndDescribesIt()
    {
        var rows = ShadowDiscrepancyCsv.Write(
        [
            Result("C1", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid, Finding("E11", "not-billable"), Finding("J9999", "terminated", line: 1)),
            Result("C2", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid),
        ], includeUndecided: false).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, rows.Length);
        Assert.Contains(",ChoFindings,CodeValidityFinding,CodesNotValidOnDateOfService,PriorAuthorizationNumber,", rows[0]);
        Assert.StartsWith("C1,", rows[1]);
        Assert.Contains($",{ShadowCodeValidity.CorePaidInvalidCode},Diagnosis ICD-10-CM E11: not-billable | Line 1 HCPCS J9999: terminated,", rows[1]);
        Assert.Equal(ShadowDiscrepancyCsv.Header.Length, rows[1].Split(',').Length);
    }

    [Fact]
    public void TheReport_GroupsCorePaidInvalidCodes_ByReason_MatchesIncluded()
    {
        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(),
        [
            Result("C1", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid, Finding("E11", "not-billable")),
            Result("C2", ShadowDiscrepancyKind.PaidVariance, ShadowOutcome.Paid, Finding("E10", "not-billable")),
            Result("C3", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid, Finding("J9999", "terminated", line: 1)),
            Result("C4", ShadowDiscrepancyKind.Match, ShadowOutcome.Paid),
        ]);

        var top = report.CorePaidWithInvalidCodes[0];
        Assert.Equal("not-billable", top.Key);
        Assert.Equal(2, top.Claims);
        Assert.Equal(120m, top.CorePaid);
        Assert.Equal("terminated", report.CorePaidWithInvalidCodes[1].Key);
    }
}
