using CloudHealthOffice.GoldenPath.Tests.Harness;
using CloudHealthOffice.Testing.Mongo;
using Claims = ClaimsService.Models;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// Phase 0 golden path: stored plan + contract/fee schedule + synthetic 837 →
/// real pricing, benefits, persistence, payment run → 835, compared with
/// <c>Golden/*.835</c>. Each scenario also asserts its amounts from the plan
/// and contract arithmetic written above it, and every 835 must satisfy the
/// X12 balancing rules (<see cref="X12835.AssertBalanced"/>) before it is
/// compared, so a golden file cannot bless an unbalanced remittance.
/// Inputs: <see cref="GoldenInputs"/>.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class GoldenPathTests
{
    private readonly GoldenPathHarness _harness;

    public GoldenPathTests(MongoRunnerFixture mongo) => _harness = new GoldenPathHarness(mongo);

    private async Task<GoldenPathResult> RunAsync(string name, Action<InMemoryAccumulatorService>? prior = null)
    {
        var result = await _harness.RunAsync(GoldenInputs.Scenario(prior), GoldenInputs.Edi837(name));
        X12835.AssertBalanced(result.Edi835);
        X12835.AssertMatchesGolden(name, result.Edi835);
        return result;
    }

    private static void AssertClaim(GoldenPathResult r, decimal charge, decimal allowed, decimal paid, decimal member)
    {
        var adj = r.FinalizedClaim.AdjudicationResult!;
        Assert.Equal(Claims.ClaimStatus.Paid, r.FinalizedClaim.Status);
        Assert.Equal(charge, r.FinalizedClaim.TotalChargeAmount);
        Assert.Equal(allowed, adj.AllowedAmount);
        Assert.Equal(paid, adj.PayerPayment);
        Assert.Equal(member, adj.PatientResponsibility);
        Assert.Equal(paid, r.PaymentRun.TotalPaymentAmount);
        Assert.Equal(paid, r.FinalizedEvent.PlanPaid);
        Assert.Equal(member, r.FinalizedEvent.MemberResponsibility);
        Assert.Equal(paid, r.FinalizedEvent.LineItems.Sum(l => l.PlanPaid));
        var clp = X12835.Segments(r.Edi835).Single(s => s[0] == "CLP");
        Assert.Equal(r.FinalizedClaim.Id, clp[7]);
    }

    // 01 — Professional office visit, deductible + coinsurance.
    // Member MBR-GOLD-01 has met $440 of the $500 deductible.
    // 99213 billed $180.00; contract rate $100.00 → CO-45 = 180 − 100 = $80.00.
    // Deductible: remaining 500 − 440 = $60.00 → PR-1 $60.00 (deductible now met).
    // Coinsurance: 20% × (100 − 60) = $8.00 → PR-2 $8.00.
    // Member 60 + 8 = $68.00 (CLP05); plan 100 − 68 = $32.00 (CLP04, SVC03, BPR02).
    // Line: 180 − (80 + 60 + 8) = 32 ✓.
    [Fact]
    public async Task OfficeVisit_DeductibleAndCoinsurance()
    {
        var r = await RunAsync("01-office-visit", GoldenInputs.Prior(deductible: 440m, oop: 440m));

        AssertClaim(r, charge: 180m, allowed: 100m, paid: 32m, member: 68m);
        Assert.Equal(60m, r.FinalizedEvent.DeductibleApplied);
        Assert.Equal(8m, r.FinalizedEvent.CoinsuranceApplied);
    }

    // 02 — Multi-line professional claim with multiple-procedure reduction.
    // Fresh plan year (nothing accumulated). All three codes carry MPI 2, so the
    // fee schedule engine ranks them by allowed amount: 29881 $1,200 (100%),
    // 11042 $200 → 50% = $100.00, 20610 $80 → 50% = $40.00.
    // Line 1 29881 billed 2,400: CO-45 1,200; deductible $500 (all of it);
    //   coinsurance 20% × 700 = $140 → member 640, plan 560.
    // Line 2 11042 billed 450: CO-45 350; coinsurance 20% × 100 = $20 → plan 80.
    // Line 3 20610 billed 150: CO-45 110; coinsurance 20% × 40 = $8 → plan 32.
    // Claim: charge 3,000; allowed 1,340; member 640 + 20 + 8 = $668;
    //   plan 560 + 80 + 32 = $672 (= 1,340 − 668).
    [Fact]
    public async Task MultiLineProfessional_MultipleProcedureReduction()
    {
        var r = await RunAsync("02-multi-line-mppr");

        AssertClaim(r, charge: 3000m, allowed: 1340m, paid: 672m, member: 668m);
        Assert.Equal(new[] { 560m, 80m, 32m },
            r.FinalizedClaim.ClaimLines.Select(l => l.AdjudicationResult!.PaidAmount));
        Assert.Equal(new[] { 1200m, 100m, 40m },
            r.FinalizedClaim.ClaimLines.Select(l => l.AdjudicationResult!.AllowedAmount));
    }

    // 03 — Percent-of-Medicare contract.
    // Medicare 99214 (POS 11, non-facility PE): (1.92 + 2.06 + 0.13) × 33.0000
    //   = 4.11 × 33 = $135.63. Contract 125%: 135.63 × 1.25 = 169.5375 → $169.54.
    // Billed $250.00 → CO-45 = $80.46. Deductible already met ($500 of $500).
    // Coinsurance 20% × 169.54 = 33.908 → $33.91 (PR-2).
    // Plan 169.54 − 33.91 = $135.63; member $33.91.
    [Fact]
    public async Task PercentOfMedicareContract()
    {
        var r = await RunAsync("03-percent-of-medicare", GoldenInputs.Prior(deductible: 500m, oop: 500m));

        AssertClaim(r, charge: 250m, allowed: 169.54m, paid: 135.63m, member: 33.91m);
    }

    // 04 — A denied line on an otherwise payable claim.
    // Deductible already met. Line 1 99213 billed 180, allowed 100: CO-45 80,
    //   coinsurance 20% × 100 = $20 → plan $80.
    // Line 2 97810 (acupuncture, not covered) billed 90, priced $60:
    //   CO-45 90 − 60 = $30 (contractual) and CO-96 $60 (non-covered), paid $0.
    // Claim: charge 270, plan $80 (CLP02 1: processed, the claim is not denied),
    //   member $20; 270 − (80 + 20 + 30 + 60) = 80 ✓.
    [Fact]
    public async Task DeniedLine_NonCoveredService()
    {
        var r = await RunAsync("04-denied-line", GoldenInputs.Prior(deductible: 500m, oop: 500m));

        AssertClaim(r, charge: 270m, allowed: 160m, paid: 80m, member: 20m);
        var denied = r.FinalizedClaim.ClaimLines[1].AdjudicationResult!;
        Assert.Equal(0m, denied.PaidAmount);
        Assert.Contains(denied.AdjustmentReasons, a => a is { GroupCode: "CO", ReasonCode: "96", Amount: 60m });
    }

    // 05 — 837I inpatient stay, DRG 470, cost share once per stay.
    // DRG case rate 6,000 × 3.0 = $18,000 (billed 42,500, so CO-45 total $24,500).
    // Cost share on the stay (fresh plan year, Inpatient Hospital benefit):
    //   deductible $500; copay $250; coinsurance 10% × (18,000 − 500 − 250) = $1,725.
    //   Member 500 + 250 + 1,725 = $2,475 (under the $3,000 OOP max); plan $15,525.
    // Line allocation (each share truncated to the cent, remainder to the last line):
    //   Allowed by billed share of 42,500: 0120 12,000 → 5,082.35; 0250 1,500 → 635.29;
    //   0360 29,000 → 12,282.35 + 0.01 remainder = 12,282.36 (Σ 18,000.00).
    //   CO-45 = billed − allowed: 6,917.65 / 864.71 / 16,717.64 (Σ 24,500.00).
    //   Deductible 500 by allowed: 141.17 / 17.64 / 341.17 + 0.02 = 341.19.
    //   Copay 250: 70.58 / 8.82 / 170.58 + 0.02 = 170.60.
    //   Coinsurance 1,725: 487.05 / 60.88 / 1,177.05 + 0.02 = 1,177.07.
    //   Member 698.80 / 87.34 / 1,688.86 (Σ 2,475.00);
    //   paid 4,383.55 / 547.95 / 10,593.50 (Σ 15,525.00).
    // SVC01: NU:0120 and NU:0250 (revenue code only), HC:27447 with SVC04 0360.
    [Fact]
    public async Task InpatientDrg_ClaimLevelCostShare()
    {
        var r = await RunAsync("05-inpatient-drg");

        AssertClaim(r, charge: 42500m, allowed: 18000m, paid: 15525m, member: 2475m);
        Assert.Equal(500m, r.FinalizedEvent.DeductibleApplied);
        Assert.Equal(250m, r.FinalizedEvent.CopayApplied);
        Assert.Equal(1725m, r.FinalizedEvent.CoinsuranceApplied);
        Assert.Equal(new[] { 4383.55m, 547.95m, 10593.50m },
            r.FinalizedClaim.ClaimLines.Select(l => l.AdjudicationResult!.PaidAmount));
        Assert.Equal(18000m, r.BenefitResult.DrgCostShare!.DrgAllowedAmount);
        Assert.All(r.BenefitResult.Lines, l => Assert.Equal("Inpatient Hospital", l.ServiceTypeCode));
    }

    // 06 — A claim that reaches the OOP max.
    // Deductible met; $2,980 of the $3,000 OOP max already used → $20 left.
    // Line 1 99214 billed 400, allowed 150: CO-45 250; coinsurance 20% × 150 = $30,
    //   OOP cap leaves $20 → OA-23 −$10; member $20, plan $130.
    // Line 2 99213 billed 180, allowed 100: CO-45 80; coinsurance $20, OOP max now
    //   met → OA-23 −$20; member $0, plan $100.
    // Claim: charge 580, allowed 250, member $20, plan $230.
    [Fact]
    public async Task OopMaxReached()
    {
        var r = await RunAsync("06-oop-max", GoldenInputs.Prior(deductible: 500m, oop: 2980m));

        AssertClaim(r, charge: 580m, allowed: 250m, paid: 230m, member: 20m);
        Assert.Equal(20m, r.FinalizedEvent.OopApplied);
    }
}
