using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Reversal (CLP02 = 22) claim and line loops from
/// <see cref="Era835ClaimPaymentBuilder.BuildReversal"/>: the original
/// remittance's loops with CLP03/04/05, SVC02/03 and every CAS amount
/// negated, so each line and the claim balance in the negative; and the
/// #1254 fallbacks for originals recorded without line adjustments.
/// </summary>
public class Era835ReversalTests
{
    private static readonly ICarcRarcMappingService Mapper =
        new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance);

    private static ClaimLineAdjustmentReasonDto Adj(string group, string reason, decimal amount, string? remark = null) =>
        new() { GroupCode = group, ReasonCode = reason, Amount = amount, RemarkCode = remark };

    /// <summary>
    /// A two-line paid claim: 300 billed, 170 paid, 50 member cost share.
    /// Line 1: 200 billed, 120 paid, CO-45 50, PR-1 20 (N130), PR-2 10.
    /// Line 2: 100 billed, 50 paid, CO-45 30, PR-1 20.
    /// </summary>
    internal static ClaimDto MultiLinePaidClaim() => new()
    {
        Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "1234567890", MemberId = "M1", ProviderName = "Acme",
        PayerClaimControlNumber = "ICN-1", Status = ClaimStatus.Paid, TotalChargeAmount = 300m,
        AdjudicationResult = new ClaimAdjudicationDto
        {
            PayerPayment = 170m, PatientResponsibility = 50m,
            AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
            {
                new() { GroupCode = "CO", ReasonCode = "45", Amount = 80m },
                new() { GroupCode = "PR", ReasonCode = "1", Amount = 40m },
                new() { GroupCode = "PR", ReasonCode = "2", Amount = 10m },
            },
        },
        ServiceLines = new List<ClaimServiceLineDto>
        {
            new()
            {
                LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1,
                AdjudicationResult = new ClaimLineAdjudicationDto
                {
                    PaidAmount = 120m,
                    AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto>
                    {
                        Adj("CO", "45", 50m), Adj("PR", "1", 20m, "N130"), Adj("PR", "2", 10m),
                    },
                },
            },
            new()
            {
                LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 100m, Units = 1,
                AdjudicationResult = new ClaimLineAdjudicationDto
                {
                    PaidAmount = 50m,
                    AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto> { Adj("CO", "45", 30m), Adj("PR", "1", 20m) },
                },
            },
        },
    };

    /// <summary>The claim's recorded payment as a payment run builds it (line CAS since #1254).</summary>
    internal static ClaimPayment Recorded(ClaimDto claim, bool denied = false) =>
        Era835ClaimPaymentBuilder.Build(claim, denied, Mapper);

    /// <summary>A recorded payment from before line CAS: the lines carry none, the header the claim-level CAS.</summary>
    internal static ClaimPayment LegacyRecorded(ClaimPayment cp)
    {
        foreach (var line in cp.ServiceLines)
        {
            line.Adjustments.Clear();
            line.RemarkCodes.Clear();
        }
        return cp;
    }

    private static List<(string GroupCode, string ReasonCode, decimal Amount)> Cas(IEnumerable<ServiceLineAdjustment> a) =>
        a.Select(x => (x.GroupCode, x.ReasonCode, x.Amount)).ToList();

    [Fact]
    public void MultiLinePaidClaim_ReversalNegatesClaimLinesAndCas_EachLineBalancesNegative()
    {
        var original = Recorded(MultiLinePaidClaim());

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, MultiLinePaidClaim(), Mapper);

        Assert.Equal("22", reversal.ClaimStatusCode);
        Assert.Equal((-300m, -170m, -50m), (reversal.ChargeAmount, reversal.PaymentAmount, reversal.PatientResponsibilityAmount));
        Assert.Equal("ICN-1", reversal.PayerClaimControlNumber);
        Assert.Empty(reversal.ClaimAdjustments);

        var line1 = reversal.ServiceLines[0];
        Assert.Equal((-200m, -120m), (line1.ChargeAmount, line1.PaymentAmount));
        Assert.Equal(new[] { ("CO", "45", -50m), ("PR", "1", -20m), ("PR", "2", -10m) }, Cas(line1.Adjustments));
        Assert.Equal("N130", line1.Adjustments[1].RemarkCode);
        var line2 = reversal.ServiceLines[1];
        Assert.Equal((-100m, -50m), (line2.ChargeAmount, line2.PaymentAmount));
        Assert.Equal(new[] { ("CO", "45", -30m), ("PR", "1", -20m) }, Cas(line2.Adjustments));

        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));

        var segments = 0;
        var edi = Era835ClaimLoops.BuildClaimLoop(reversal, ref segments);
        Assert.StartsWith("CLP*CLM-1*22*-300.00*-170.00*-50.00*HM*ICN-1~", edi);
        Assert.Contains("SVC*HC:99213*-200.00*-120.00**1~CAS*CO*45*-50.00~CAS*PR*1*-20.00**2*-10.00~LQ*HE*N130~", edi);
        Assert.Contains("SVC*HC:85025*-100.00*-50.00**1~CAS*CO*45*-30.00~CAS*PR*1*-20.00~", edi);
        EdiBalance.AssertEveryLoopBalances(edi);

        // The original is left as recorded.
        Assert.Equal(("1", 170m, 50m), (original.ClaimStatusCode, original.PaymentAmount, original.ServiceLines[0].Adjustments[0].Amount));
    }

    [Fact]
    public void DeniedClaim_ReversalNegatesDenialCas_Clp04StaysZero_Balanced()
    {
        // Denied at line level: each line 0 paid, CO-50 for its charge, M51 on line 1.
        var claim = new ClaimDto
        {
            Id = "d1", ClaimNumber = "CLM-D1", BillingProviderNPI = "1234567890", Status = ClaimStatus.Denied,
            TotalChargeAmount = 300m,
            AdjudicationResult = new ClaimAdjudicationDto
            {
                PayerPayment = 0m, DenialReasonCode = "50", RemarkCodes = new List<string> { "N115" },
            },
            ServiceLines = new List<ClaimServiceLineDto>
            {
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, PaidAmount = 0m, Units = 1,
                    AdjudicationResult = new ClaimLineAdjudicationDto
                    {
                        PaidAmount = 0m,
                        AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto> { Adj("CO", "50", 200m, "M51") },
                    },
                },
                new()
                {
                    LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 100m, PaidAmount = 0m, Units = 1,
                    AdjudicationResult = new ClaimLineAdjudicationDto
                    {
                        PaidAmount = 0m,
                        AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto> { Adj("CO", "50", 100m) },
                    },
                },
            },
        };
        var original = Recorded(claim, denied: true);
        Assert.Equal("4", original.ClaimStatusCode);

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        Assert.Equal("22", reversal.ClaimStatusCode);
        Assert.Equal((-300m, 0m, 0m), (reversal.ChargeAmount, reversal.PaymentAmount, reversal.PatientResponsibilityAmount));
        Assert.Equal(new[] { ("CO", "50", -200m) }, Cas(reversal.ServiceLines[0].Adjustments));
        Assert.Equal(new[] { ("CO", "50", -100m) }, Cas(reversal.ServiceLines[1].Adjustments));
        Assert.Equal(new[] { "N115" }, reversal.RemarkCodes);
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));

        var segments = 0;
        var edi = Era835ClaimLoops.BuildClaimLoop(reversal, ref segments);
        Assert.StartsWith("CLP*CLM-D1*22*-300.00*0.00*0.00*", edi); // never -0.00
        Assert.Contains("MOA***N115~", edi);
        Assert.Contains("SVC*HC:99213*-200.00*0.00**1~CAS*CO*50*-200.00~LQ*HE*M51~", edi);
        EdiBalance.AssertEveryLoopBalances(edi);
    }

    [Fact]
    public void DeniedLegacy_WithoutLineDetail_FallsBackToCoDenialCarc_Negated()
    {
        var claim = new ClaimDto
        {
            Id = "d2", ClaimNumber = "CLM-D2", BillingProviderNPI = "1234567890", Status = ClaimStatus.Denied,
            TotalChargeAmount = 150m,
            AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 0m, DenialReasonCode = "96" },
            ServiceLines = new List<ClaimServiceLineDto>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, PaidAmount = 0m, Units = 1 },
                new() { LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 50m, PaidAmount = 0m, Units = 1 },
            },
        };
        var original = LegacyRecorded(Recorded(claim, denied: true));
        original.ClaimAdjustments = new List<ClaimAdjustment> { new() { GroupCode = "CO", ReasonCode = "96", Amount = 150m } };

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        Assert.Empty(reversal.ClaimAdjustments);
        Assert.Equal(new[] { ("CO", "96", -100m) }, Cas(reversal.ServiceLines[0].Adjustments));
        Assert.Equal(new[] { ("CO", "96", -50m) }, Cas(reversal.ServiceLines[1].Adjustments));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
    }

    /// <summary>
    /// A claim whose lines need SVC01 detail: a modifier (11042-51), and an
    /// institutional line billed with a three-character revenue code plus HCPCS.
    /// </summary>
    private static ClaimDto ClaimWithModifiersAndRevenueCode()
    {
        var claim = MultiLinePaidClaim();
        claim.ServiceLines![0].ProcedureCode = "11042";
        claim.ServiceLines[0].Modifiers = new List<string> { "51" };
        claim.ServiceLines[1].ProcedureCode = "27447";
        claim.ServiceLines[1].Modifiers = new List<string> { "RT" };
        claim.ServiceLines[1].RevenueCode = "360";
        return claim;
    }

    /// <summary>SVC01 and SVC04 of each 2110 loop: the billed-service identity.</summary>
    private static List<(string Svc01, string Svc04)> ServiceIdentities(ClaimPayment cp)
    {
        var segments = 0;
        return Era835ClaimLoops.BuildClaimLoop(cp, ref segments)
            .Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('*'))
            .Where(e => e[0] == "SVC")
            .Select(e => (e[1], e[4]))
            .ToList();
    }

    [Theory]
    [InlineData(false)] // original recorded with line CAS
    [InlineData(true)]  // legacy original, adjustments re-derived
    public void Reversal_Svc01AndSvc04_IdentifyTheSameServiceAsTheOriginal(bool legacy)
    {
        var claim = ClaimWithModifiersAndRevenueCode();
        var original = Recorded(claim);
        if (legacy)
            original = LegacyRecorded(original);
        Assert.Equal(!legacy, Era835ClaimPaymentBuilder.RecordedWithAdjustments(original));

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        var expected = new[] { ("HC:11042:51", ""), ("HC:27447:RT", "0360") };
        Assert.Equal(expected, ServiceIdentities(original));
        Assert.Equal(expected, ServiceIdentities(reversal));
        Assert.Equal(ServiceIdentities(original), ServiceIdentities(reversal));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
    }

    [Fact]
    public void LegacyMultiLine_WithoutLineDetail_FallsBackToCo45_OrTheNcciCarc()
    {
        // Paid before line CAS: the recorded lines carry none; claims-service
        // holds no line adjustments; line 2 was cut by an NCCI edit (CO-236).
        var claim = MultiLinePaidClaim();
        foreach (var line in claim.ServiceLines!)
            line.AdjudicationResult!.AdjustmentReasons = null;
        claim.PendDetails = new PendDetailsDto
        {
            PendCode = "NCCI",
            EditFailures = new List<EditFailureDto>
            {
                new() { EditType = "NCCI_PAIR", RuleId = "R1", SuggestedCarc = "236", SuggestedRarc = "M80", AffectedLineNumbers = new List<int> { 2 } },
            },
        };
        var original = LegacyRecorded(Recorded(claim));
        original.ClaimAdjustments = claim.AdjudicationResult!.AdjustmentReasons!
            .Select(r => new ClaimAdjustment { GroupCode = r.GroupCode, ReasonCode = r.ReasonCode, Amount = r.Amount })
            .ToList();
        Assert.False(Era835ClaimPaymentBuilder.RecordedWithAdjustments(original));

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        Assert.Equal(new[] { ("CO", "45", -80m) }, Cas(reversal.ServiceLines[0].Adjustments));
        Assert.Equal(new[] { ("CO", "236", -50m) }, Cas(reversal.ServiceLines[1].Adjustments));
        Assert.Equal("M80", reversal.ServiceLines[1].Adjustments[0].RemarkCode);
        // The lines explain the charge: the claim-level totals are not repeated in the header.
        Assert.Empty(reversal.ClaimAdjustments);
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
        var segments = 0;
        EdiBalance.AssertEveryLoopBalances(Era835ClaimLoops.BuildClaimLoop(reversal, ref segments));
    }

    [Fact]
    public void LegacySingleLine_WithoutLineDetail_TakesTheClaimLevelCas_Negated()
    {
        var claim = new ClaimDto
        {
            Id = "s1", ClaimNumber = "CLM-S1", BillingProviderNPI = "1234567890", Status = ClaimStatus.Paid,
            TotalChargeAmount = 1000m,
            AdjudicationResult = new ClaimAdjudicationDto
            {
                PayerPayment = 650m, PatientResponsibility = 200m,
                AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
                {
                    new() { GroupCode = "PR", ReasonCode = "1", Amount = 200m },
                    new() { GroupCode = "CO", ReasonCode = "45", Amount = 150m },
                },
            },
            ServiceLines = new List<ClaimServiceLineDto>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 1000m, PaidAmount = 650m, Units = 1 },
            },
        };
        var original = LegacyRecorded(Recorded(claim));

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        Assert.Equal(new[] { ("PR", "1", -200m), ("CO", "45", -150m) }, Cas(reversal.ServiceLines[0].Adjustments));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
    }

    [Fact]
    public void ReversalPlusCorrectedRepayment_SameEnvelope_BothLoopsBalance_BprIsTheNet()
    {
        var generator = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        var reversal = Era835ClaimPaymentBuilder.BuildReversal(Recorded(MultiLinePaidClaim()), MultiLinePaidClaim(), Mapper);

        // The corrected claim pays line 2 in full (100) and keeps line 1: 220 paid.
        var corrected = MultiLinePaidClaim();
        corrected.Id = "c1-new";
        corrected.ClaimNumber = "CLM-1";
        corrected.AdjudicationResult!.PayerPayment = 220m;
        corrected.AdjudicationResult.PatientResponsibility = 30m;
        corrected.ServiceLines![1].AdjudicationResult!.PaidAmount = 100m;
        corrected.ServiceLines[1].AdjudicationResult!.AdjustmentReasons = null;
        var repayment = Recorded(corrected);

        var partners = new Dictionary<string, TradingPartnerInfo>
        {
            ["TP-A"] = new()
            {
                OriginatingCompanyId = "1123456789", PayerRoutingNumber = "011000015", PayerAccountNumber = "12345",
                PayeeRoutingNumber = "021000021", PayeeAccountNumber = "67890",
            },
        };
        var envelope = generator.GenerateBatch(new[]
        {
            Input(-170m, "R-0001", reversal, isReversal: true),
            Input(220m, "0001000001", repayment, isReversal: false),
        }, partners).Single();

        Assert.Equal(50m, envelope.TotalPaymentAmount);
        Assert.Equal(0m, envelope.ForwardBalanceAmount);
        var edi = envelope.EdiContent;
        Assert.Contains("CLP*CLM-1*22*-300.00*-170.00*-50.00*", edi);
        Assert.Contains("CLP*CLM-1*1*300.00*220.00*30.00*", edi);
        Assert.DoesNotContain("PLB*", edi);
        EdiBalance.AssertEveryLoopBalances(edi);
    }

    [Fact]
    public void ReversalPlusSmallerRepayment_NetNegative_ForwardBalanceStillCarried()
    {
        var generator = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        var reversal = Era835ClaimPaymentBuilder.BuildReversal(Recorded(MultiLinePaidClaim()), MultiLinePaidClaim(), Mapper);
        var corrected = MultiLinePaidClaim();
        corrected.Id = "c1-new";
        corrected.AdjudicationResult!.PayerPayment = 120m;
        corrected.ServiceLines![1].AdjudicationResult!.PaidAmount = 0m;
        corrected.ServiceLines[1].AdjudicationResult!.AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto> { Adj("CO", "97", 100m) };
        var repayment = Recorded(corrected);

        var envelope = generator.GenerateBatch(new[]
        {
            Input(-170m, "R-0001", reversal, isReversal: true),
            Input(120m, "0001000001", repayment, isReversal: false),
        }, new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = new() { OriginatingCompanyId = "1123456789" } }).Single();

        Assert.Equal(0m, envelope.TotalPaymentAmount);
        Assert.Equal(-50m, envelope.ForwardBalanceAmount);
        Assert.Contains("*FB:R-0001*-50.00", envelope.EdiContent);
        EdiBalance.AssertEveryLoopBalances(envelope.EdiContent);
    }

    [Fact]
    public void ReversalWithUnbalancedRecordedLineCas_IsReportedAndRefusedByGeneration()
    {
        var original = Recorded(MultiLinePaidClaim());
        original.ServiceLines[0].Adjustments[0].Amount = 40m; // 200 - 70 != 120

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, MultiLinePaidClaim(), Mapper);

        var problems = Era835FinancialSegments.AdjustmentBalanceProblems(reversal);
        Assert.Contains(problems, p => p.Contains("line 1") && p.Contains("-120.00"));
        var generator = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        Assert.Throws<InvalidOperationException>(() => generator.GenerateBatch(
            new[] { Input(-170m, "R-0001", reversal, isReversal: true) },
            new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = new() { OriginatingCompanyId = "1123456789" } }));
    }

    private static ClaimPayment ClaimLevel(string status, decimal charge, decimal paid, params decimal[] cas) => new()
    {
        ClaimId = "cl1", PatientControlNumber = "CLM-CL1", ClaimStatusCode = status,
        ChargeAmount = charge, PaymentAmount = paid,
        ClaimAdjustments = cas.Select(a => new ClaimAdjustment { GroupCode = "CO", ReasonCode = "45", Amount = a }).ToList(),
    };

    private static readonly Dictionary<string, TradingPartnerInfo> NonPartner =
        new() { ["TP-A"] = new() { OriginatingCompanyId = "1123456789" } };

    [Fact]
    public void ClaimLevelReversal_HeaderCasNotExplainingTheCharge_ReportedAndRefusedByGeneration()
    {
        // Recorded at claim level: CLP03 100, CLP04 80, CAS 10 (10 unexplained).
        var original = ClaimLevel("1", 100m, 80m, 10m);

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, new ClaimDto { Id = "cl1" }, Mapper);

        Assert.Equal((-100m, -80m, -10m), (reversal.ChargeAmount, reversal.PaymentAmount, reversal.ClaimAdjustments[0].Amount));
        var problem = Assert.Single(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
        Assert.Contains("CLP03", problem);
        Assert.Contains("-90.00", problem);
        var generator = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        Assert.Throws<InvalidOperationException>(() => generator.GenerateBatch(
            new[] { Input(-80m, "R-0001", reversal, isReversal: true) }, NonPartner));
    }

    [Fact]
    public void ClaimLevelReversal_Balanced_Accepted()
    {
        var reversal = Era835ClaimPaymentBuilder.BuildReversal(ClaimLevel("1", 100m, 80m, 20m), new ClaimDto { Id = "cl1" }, Mapper);

        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
        var generator = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        var edi = generator.GenerateBatch(new[] { Input(-80m, "R-0001", reversal, isReversal: true) }, NonPartner).Single().EdiContent;
        Assert.Contains("CLP*CLM-CL1*22*-100.00*-80.00*0.00*HM*cl1~CAS*CO*45*-20.00~", edi);
        EdiBalance.AssertEveryLoopBalances(edi);
    }

    [Fact]
    public void ClaimLevelPayment_HeaderCasNotExplainingTheCharge_Reported()
    {
        Assert.Single(Era835FinancialSegments.AdjustmentBalanceProblems(ClaimLevel("1", 100m, 80m, 10m)));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(ClaimLevel("1", 100m, 80m, 20m)));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(ClaimLevel("1", 100m, 100m)));
    }

    [Fact]
    public void StoredLegacyReversals_PositiveClp03_NoLineCas_StillRegenerate()
    {
        // Stored before this change: CLP03 positive, CLP04 and CAS negated.
        var claimLevel = ClaimLevel("22", 1000m, -800m, -200m);
        var withLines = ClaimLevel("22", 1000m, -800m, -200m);
        withLines.ClaimId = "cl2";
        withLines.ServiceLines.Add(new ServiceLinePayment { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 1000m, PaymentAmount = -800m, Units = 1 });
        Assert.True(Era835FinancialSegments.IsLegacyReversal(claimLevel));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(claimLevel));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(withLines));

        var era = new EraGeneratorService(NullLogger<EraGeneratorService>.Instance);
        foreach (var cp in new[] { claimLevel, withLines })
        {
            var payment = Input(-800m, "R-OLD", cp, isReversal: true).Payment;
            Assert.Contains("CLP*CLM-CL1*22*1000.00*-800.00*", era.Generate835(payment, NonPartner["TP-A"]));
        }
    }

    private static EraPaymentInput Input(decimal total, string check, ClaimPayment cp, bool isReversal) => new()
    {
        TradingPartnerId = "TP-A",
        IsReversal = isReversal,
        Payment = new Payment
        {
            CheckNumber = check,
            PaymentMethod = "ACH",
            TotalPaymentAmount = total,
            PaymentDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Cloud Health Office",
            PayeeName = "Acme",
            PayeeNPI = "1234567890",
            IsReversal = isReversal,
            ClaimPayments = new List<ClaimPayment> { cp },
        },
    };
}

/// <summary>Balance checks on emitted 835 text, independent of the generator's own check.</summary>
internal static class EdiBalance
{
    /// <summary>
    /// For every CLP loop: each SVC satisfies SVC02 - sum(its CAS) = SVC03,
    /// and CLP03 - sum(every CAS in the loop) = CLP04.
    /// </summary>
    public static void AssertEveryLoopBalances(string edi)
    {
        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();
        string[]? clp = null, svc = null;
        decimal claimCas = 0m, lineCas = 0m;
        var loops = 0;

        void CloseLine()
        {
            if (svc is null) return;
            Assert.True(decimal.Parse(svc[2]) - lineCas == decimal.Parse(svc[3]),
                $"SVC {string.Join("*", svc)} does not balance: line CAS {lineCas}");
            svc = null;
            lineCas = 0m;
        }
        void CloseClaim()
        {
            CloseLine();
            if (clp is null) return;
            Assert.True(decimal.Parse(clp[3]) - claimCas == decimal.Parse(clp[4]),
                $"CLP {string.Join("*", clp)} does not balance: CAS {claimCas}");
            clp = null;
            claimCas = 0m;
            loops++;
        }

        foreach (var s in segments)
        {
            switch (s[0])
            {
                case "CLP":
                    CloseClaim();
                    clp = s;
                    break;
                case "SVC":
                    CloseLine();
                    svc = s;
                    break;
                case "CAS":
                    // CAS01 group, then reason/amount/quantity triplets.
                    var amount = Enumerable.Range(0, (s.Length - 1) / 3 + 1)
                        .Select(k => 3 + 3 * k)
                        .Where(i => i < s.Length && s[i] != string.Empty)
                        .Sum(i => decimal.Parse(s[i]));
                    claimCas += amount;
                    if (svc is not null) lineCas += amount;
                    break;
                case "PLB":
                case "SE":
                    CloseClaim();
                    break;
            }
        }
        CloseClaim();
        Assert.True(loops > 0, "no CLP loop");
    }
}
