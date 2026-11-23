using ClaimsService.Services.Adjudication.Shadow;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowDenialVerificationTests
{
    [Fact]
    public void EachCoreDeniedClaim_GetsOneVerdict()
    {
        var results = new List<ShadowAdjudicationResult>
        {
            DeniedClaim("D1", "197", choOutcome: ShadowOutcome.Paid, choCode: null, choPaid: 120m, charge: 300m,
                kind: ShadowDiscrepancyKind.OutcomeMismatch),
            DeniedClaim("D2", "197", choOutcome: ShadowOutcome.Denied, choCode: "197", choPaid: 0m, charge: 80m,
                kind: ShadowDiscrepancyKind.Match),
            DeniedClaim("D3", "16", choOutcome: ShadowOutcome.Denied, choCode: "96", choPaid: 0m, charge: 50m,
                kind: ShadowDiscrepancyKind.DenialCodeMismatch),
            DeniedClaim("D4", "16", choOutcome: ShadowOutcome.Pended, choCode: null, choPaid: 0m, charge: 40m,
                kind: ShadowDiscrepancyKind.ChoUndecided),
            // Not final at the core: no denial to verify.
            DeniedClaim("D5", "16", choOutcome: ShadowOutcome.Paid, choCode: null, choPaid: 10m, charge: 10m,
                kind: ShadowDiscrepancyKind.NotComparable),
        };

        var v = ShadowDenialVerificationBuilder.Build(results, topN: 10);

        Assert.Equal(4, v.CoreDenials);
        Assert.Equal(4, v.Claims);
        Assert.Equal(1, v.ChoWouldPay.Denials);
        Assert.Equal(120m, v.ChoWouldPay.ChoWouldPayAmount);
        Assert.Equal(300m, v.ChoWouldPay.Billed);
        Assert.Equal(["D1"], v.ChoWouldPay.ExampleClaims);
        Assert.Equal(1, v.ChoConfirms.Denials);
        Assert.Equal(0m, v.ChoConfirms.ChoWouldPayAmount);
        Assert.Equal(1, v.ChoDeniesForAnotherReason.Denials);
        Assert.Equal(1, v.ChoUndecided.Denials);
        // One confirmed of three decided.
        Assert.Equal(0.3333m, v.ConfirmRate);
    }

    [Fact]
    public void ByCoreDenialCode_PutsTheMostCHOWouldPayDollarsFirst_AndNamesCHOsCodes()
    {
        var results = new List<ShadowAdjudicationResult>
        {
            DeniedClaim("A", "16", ShadowOutcome.Denied, "96", 0m, 10m, ShadowDiscrepancyKind.DenialCodeMismatch),
            DeniedClaim("B", "16", ShadowOutcome.Denied, "96", 0m, 10m, ShadowDiscrepancyKind.DenialCodeMismatch),
            DeniedClaim("C", "16", ShadowOutcome.Denied, "16", 0m, 10m, ShadowDiscrepancyKind.Match),
            DeniedClaim("D", "197", ShadowOutcome.Paid, null, 75m, 90m, ShadowDiscrepancyKind.OutcomeMismatch),
            DeniedClaim("E", " ", ShadowOutcome.Denied, "18", 0m, 5m, ShadowDiscrepancyKind.DenialCodeMismatch),
        };

        var rows = ShadowDenialVerificationBuilder.Build(results, topN: 10).ByCoreDenialCode;

        Assert.Equal(["197", "16", "none"], rows.Select(r => r.CoreDenialCode));
        var carc197 = rows[0];
        Assert.Equal(1, carc197.ChoWouldPay);
        Assert.Equal(75m, carc197.ChoWouldPayAmount);
        Assert.Equal(0m, carc197.ConfirmRate);

        var carc16 = rows[1];
        Assert.Equal(3, carc16.Denials);
        Assert.Equal(2, carc16.ChoDeniesForAnotherReason);
        Assert.Equal(1, carc16.ChoConfirms);
        Assert.Equal(["96 (2)"], carc16.ChoCodesInstead);
        Assert.Equal(0.3333m, carc16.ConfirmRate);
    }

    [Fact]
    public void LinesTheCoreDeniedOnAPaidClaim_AreVerifiedOneByOne()
    {
        var claim = new ShadowAdjudicationResult
        {
            Id = "P1", ClaimNumber = "P1",
            Kind = ShadowDiscrepancyKind.PaidVariance,
            CoreOutcome = ShadowOutcome.Paid, ChoOutcome = ShadowOutcome.Paid,
            Lines =
            [
                new ShadowLineComparison { LineNumber = 1, Charge = 100m, CorePaid = 80m, ChoPaid = 80m },
                new ShadowLineComparison { LineNumber = 2, Charge = 60m, CorePaid = 0m, ChoPaid = 45m, CoreDenialCode = "B7" },
                new ShadowLineComparison { LineNumber = 3, Charge = 30m, CorePaid = 0m, ChoPaid = 0m, CoreDenialCode = "97", ChoDenialCode = "97" },
            ],
        };

        var v = ShadowDenialVerificationBuilder.Build([claim], topN: 10);

        Assert.Equal(2, v.CoreDenials);
        Assert.Equal(1, v.Claims);
        Assert.Equal(1, v.ChoWouldPay.Denials);
        Assert.Equal(45m, v.ChoWouldPay.ChoWouldPayAmount);
        Assert.Equal(60m, v.ChoWouldPay.Billed);
        Assert.Equal(1, v.ChoConfirms.Denials);
        Assert.Equal(30m, v.ChoConfirms.Billed);
    }

    [Fact]
    public void LineDenialsOnAClaimCHOCouldNotDecide_AreUndecided()
    {
        var claim = new ShadowAdjudicationResult
        {
            Id = "U1", ClaimNumber = "U1",
            Kind = ShadowDiscrepancyKind.ChoUndecided,
            CoreOutcome = ShadowOutcome.Paid, ChoOutcome = ShadowOutcome.Pended,
            Lines = [new ShadowLineComparison { LineNumber = 1, Charge = 20m, CoreDenialCode = "4" }],
        };

        var v = ShadowDenialVerificationBuilder.Build([claim], topN: 10);

        Assert.Equal(1, v.ChoUndecided.Denials);
        Assert.Null(v.ConfirmRate);
        Assert.Null(v.ByCoreDenialCode.Single().ConfirmRate);
    }

    [Fact]
    public void TheRunReport_CarriesTheDenialVerification()
    {
        var results = new List<ShadowAdjudicationResult>
        {
            DeniedClaim("D1", "197", ShadowOutcome.Paid, null, 120m, 300m, ShadowDiscrepancyKind.OutcomeMismatch),
        };

        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(), results);

        Assert.Equal(1, report.DenialVerification.CoreDenials);
        Assert.Equal(120m, report.DenialVerification.ChoWouldPay.ChoWouldPayAmount);
    }

    private static ShadowAdjudicationResult DeniedClaim(
        string number, string coreCode, string choOutcome, string? choCode, decimal choPaid, decimal charge, string kind) => new()
    {
        Id = number,
        ClaimNumber = number,
        Kind = kind,
        CoreOutcome = kind == ShadowDiscrepancyKind.NotComparable ? ShadowOutcome.NotFinal : ShadowOutcome.Denied,
        ChoOutcome = choOutcome,
        CoreDenialCode = coreCode,
        ChoDenialCode = choCode,
        ChoPaid = choPaid,
        Lines = [new ShadowLineComparison { LineNumber = 1, Charge = charge, CoreDenialCode = coreCode, ChoDenialCode = choCode }],
    };
}
