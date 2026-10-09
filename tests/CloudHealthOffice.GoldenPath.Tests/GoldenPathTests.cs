using CloudHealthOffice.BenefitEngine.Domain;
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

    private async Task<GoldenPathResult> RunAsync(
        string name, Action<InMemoryAccumulatorService>? prior = null, string? planDocument = null)
    {
        var scenario = GoldenInputs.Scenario(prior);
        if (planDocument is not null)
        {
            scenario = new GoldenScenario
            {
                PlanDocument = planDocument,
                FeeSchedules = scenario.FeeSchedules,
                Contracts = scenario.Contracts,
                CategoryMappings = scenario.CategoryMappings,
                PriorAccumulators = scenario.PriorAccumulators,
            };
        }
        var result = await _harness.RunAsync(scenario, GoldenInputs.Edi837(name));
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
    // The cap reduces the patient-responsibility amount itself (no OA-23).
    // Line 1 99214 billed 400, allowed 150: CO-45 250; coinsurance 20% × 150 = $30,
    //   OOP cap leaves $20 → PR-2 reduced to $20; member $20, plan $130
    //   (400 − 250 − 20 = 130 ✓).
    // Line 2 99213 billed 180, allowed 100: CO-45 80; coinsurance $20, OOP max now
    //   met → PR-2 reduced to $0 and dropped; member $0, plan $100 (180 − 80 = 100 ✓).
    // Claim: charge 580, allowed 250, member $20, plan $230.
    [Fact]
    public async Task OopMaxReached()
    {
        var r = await RunAsync("06-oop-max", GoldenInputs.Prior(deductible: 500m, oop: 2980m));

        AssertClaim(r, charge: 580m, allowed: 250m, paid: 230m, member: 20m);
        Assert.Equal(20m, r.FinalizedEvent.OopApplied);
        Assert.Equal(20m, r.FinalizedEvent.CoinsuranceApplied);
        Assert.Equal(new[] { 130m, 100m },
            r.FinalizedClaim.ClaimLines.Select(l => l.AdjudicationResult!.PaidAmount));
        Assert.Equal(
            new[] { ("CO", "45", 250m), ("PR", "2", 20m) },
            r.FinalizedClaim.ClaimLines[0].AdjudicationResult!.AdjustmentReasons
                .Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal(
            new[] { ("CO", "45", 80m) },
            r.FinalizedClaim.ClaimLines[1].AdjudicationResult!.AdjustmentReasons
                .Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
    }

    // 07 — We are the TERTIARY payer (2000B SBR*T) on a two-line claim.
    // Member has met $440 of the $500 deductible ($60 left), OOP $440.
    // Our allowed: 99214 $150 (billed 400, CO-45 250); 99213 $100 (billed 180, CO-45 80).
    // Primary (2320 SBR*P, OTHERPAYER1) reported line level (2430):
    //   L1 SVD02 $40, PR-1 $100; L2 SVD02 $72, PR-2 $18 (AMT*D $112).
    // Secondary (2320 SBR*S, OTHERPAYER2) reported claim level only: AMT*D $60,
    //   CAS OA-23 462, PR-1 50, PR-2 8 (PR $58). No 2430 → prorated by charge
    //   400 : 180 → paid L1 60 × 400/580 = 41.379 → $41.37, L2 $18.63;
    //   PR L1 58 × 400/580 = $40.00, L2 $18.00.
    // L1: pre-COB deductible $60 + 20% × 90 = $18 → cost share $78, normal benefit $72.
    //   Prior paid 40 + 41.37 = $81.37; the member owes $40.00 after the secondary,
    //   so balance = min(150 − 81.37, 40.00) = $40.00. We pay min(72, 40) = $40.00;
    //   member min(78, 40 − 40) = $0. OA-23 = 150 − 0 − 40 = $110.00.
    //   SVC: 400 − (250 + 110) = 40 ✓.
    // L2: under NAIC full credit the $60 credited on L1 meets the deductible
    //   for L2 too: coinsurance 20% × 100 = $20, normal $80. Prior paid
    //   72 + 18.63 = $90.63; balance = min(100 − 90.63, 18.00) = $9.37. We pay
    //   min(80, 9.37) = $9.37, member $0, OA-23 = 100 − 9.37 = $90.63.
    //   SVC: 180 − (80 + 90.63) = 9.37 ✓. (Under MemberPaidOnly L2 still
    //   meets $60 of deductible — normal $32 — but the $9.37 balance binds
    //   first, so this 835 is the same in both modes.)
    // Claim: charge 580, allowed 250, plan $49.37 (CLP04, BPR02), member $0;
    //   CLP02 = 3 (processed as tertiary).
    // Accumulators (NAIC full credit, the plan default): deductible +$60 —
    //   what we would have applied with no other coverage, capped at the $60
    //   left — even though the member owes no PR-1; OOP +$0.
    [Fact]
    public async Task TertiaryCob_TwoPriorPayers_LineAndClaimLevel()
    {
        var r = await RunAsync("07-tertiary-cob", GoldenInputs.Prior(deductible: 440m, oop: 440m));

        AssertClaim(r, charge: 580m, allowed: 250m, paid: 49.37m, member: 0m);
        Assert.Equal("T", r.FinalizedClaim.PayerResponsibilityCode);
        Assert.Equal(new[] { "P", "S" }, r.FinalizedClaim.OtherPayers.Select(p => p.PayerResponsibilityCode));
        Assert.Equal(new[] { 40m, 9.37m },
            r.FinalizedClaim.ClaimLines.Select(l => l.AdjudicationResult!.PaidAmount));
        Assert.Equal(
            new[] { ("CO", "45", 250m), ("OA", "23", 110m) },
            r.FinalizedClaim.ClaimLines[0].AdjudicationResult!.AdjustmentReasons
                .Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal(
            new[] { ("CO", "45", 80m), ("OA", "23", 90.63m) },
            r.FinalizedClaim.ClaimLines[1].AdjudicationResult!.AdjustmentReasons
                .Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal("3", X12835.Segments(r.Edi835).Single(s => s[0] == "CLP")[2]);

        // The member owes nothing; the deductible is credited with the $60
        // our plan applied before COB (NAIC MDL-120 §7), the OOP with $0.
        Assert.Equal(0m, r.FinalizedEvent.DeductibleApplied);
        Assert.Equal(60m, r.FinalizedEvent.DeductibleCredited);
        Assert.Equal(new decimal?[] { 60m, null }, r.FinalizedEvent.LineItems.Select(l => l.DeductibleCredited));
        Assert.Equal(0m, r.FinalizedEvent.OopApplied);
        Assert.Equal(60m, Applied(r, AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, Applied(r, AccumulatorType.IndividualOutOfPocketMax));
    }

    // 07 again with the plan set to MemberPaidOnly: the deductible setting
    // changes only the accumulators. The 835 matches the same golden file
    // byte for byte; the deductible is credited with what the member owes ($0).
    [Fact]
    public async Task TertiaryCob_MemberPaidOnlyPlan_Same835_DeductibleNotCredited()
    {
        var plan = GoldenInputs.PlanDocument.Replace(
            "\"familyAccumulatorModel\": \"Embedded\",",
            "\"familyAccumulatorModel\": \"Embedded\",\n  \"cobDeductibleCredit\": \"MemberPaidOnly\",");
        Assert.Contains("MemberPaidOnly", plan);

        var r = await RunAsync("07-tertiary-cob", GoldenInputs.Prior(deductible: 440m, oop: 440m), plan);

        AssertClaim(r, charge: 580m, allowed: 250m, paid: 49.37m, member: 0m);
        Assert.Null(r.FinalizedEvent.DeductibleCredited);
        Assert.All(r.FinalizedEvent.LineItems, l => Assert.Null(l.DeductibleCredited));
        Assert.Equal(0m, Applied(r, AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, Applied(r, AccumulatorType.IndividualOutOfPocketMax));
    }

    private static decimal Applied(GoldenPathResult r, AccumulatorType type) =>
        r.BenefitResult.AccumulatorSnapshot
            .Single(s => s.Type == type && s.Scope == AccumulatorScope.Individual
                && s.NetworkTier == CloudHealthOffice.BenefitEngine.Domain.NetworkTier.InNetwork)
            .AmountApplied;
}
