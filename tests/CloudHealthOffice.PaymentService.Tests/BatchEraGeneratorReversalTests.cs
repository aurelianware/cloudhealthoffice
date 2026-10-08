using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Capability 5.12b — covers <see cref="BatchEraGeneratorService"/>'s
/// reversal-mode flag threading. Per Plan-First Premise C, the
/// generator does NOT branch on reversal mode for segment emission
/// (CLP02="22" and CAS sign-flips are set upstream by
/// <c>ReversalRunService</c>); the generator only threads the
/// <see cref="EraPaymentInput.IsReversal"/> flag through to the
/// <see cref="EraEnvelope.IsReversal"/> result so the caller persists
/// with <see cref="EraEnvelopeRecord.ReversalRunId"/> set.
/// </summary>
public class BatchEraGeneratorReversalTests
{
    private readonly BatchEraGeneratorService _generator =
        new(NullLogger<BatchEraGeneratorService>.Instance);

    private static IReadOnlyDictionary<string, TradingPartnerInfo> Partners(string id) =>
        new Dictionary<string, TradingPartnerInfo>
        {
            [id] = new()
            {
                InterchangeSenderId = "S",
                InterchangeReceiverId = "R",
                ApplicationSenderId = "S",
                ApplicationReceiverId = "R",
                OriginatingCompanyId = "1123456789",
            },
        };

    private static EraPaymentInput ReversalInput(string tpId, decimal amount, string clp02 = "22")
    {
        return new EraPaymentInput
        {
            TradingPartnerId = tpId,
            IsReversal = true,
            Payment = new Payment
            {
                CheckNumber = "R-CHK001",
                PaymentMethod = "ACH",
                TotalPaymentAmount = amount,
                PaymentDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                PayerName = "Cloud Health Office",
                PayerId = "CHO",
                PayeeName = "Acme Health",
                PayeeNPI = "1234567890",
                IsReversal = true,
                ClaimPayments = new List<ClaimPayment>
                {
                    new()
                    {
                        ClaimId = "c-rev",
                        PatientControlNumber = "CLM-001",
                        ClaimStatusCode = clp02,
                        ChargeAmount = 1000m,
                        PaymentAmount = amount,
                        PatientResponsibilityAmount = -200m,
                        ClaimAdjustments = new List<ClaimAdjustment>
                        {
                            new() { GroupCode = "PR", ReasonCode = "1", Amount = -200m },
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void GenerateBatch_AllInputsReversal_EnvelopeMarkedReversal()
    {
        var inputs = new[] { ReversalInput("TP-A", -800m) };

        var envelopes = _generator.GenerateBatch(inputs, Partners("TP-A"));

        Assert.Single(envelopes);
        Assert.True(envelopes[0].IsReversal);
        Assert.Equal(0m, envelopes[0].TotalPaymentAmount);       // BPR02 is never negative
        Assert.Equal(-800m, envelopes[0].ForwardBalanceAmount);  // carried forward (PLB FB)
    }

    private static List<string[]> Segments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();

    /// <summary>(code, reference, amount) of every PLB adjustment pair.</summary>
    private static List<(string Code, string Reference, decimal Amount)> Plbs(List<string[]> segments) =>
        segments.Where(s => s[0] == "PLB")
            .SelectMany(s => Enumerable.Range(0, (s.Length - 3) / 2)
                .Select(k => (Id: s[3 + 2 * k].Split(':'), Amount: decimal.Parse(s[4 + 2 * k])))
                .Select(x => (x.Id[0], x.Id.Length > 1 ? x.Id[1] : string.Empty, x.Amount)))
            .ToList();

    private static void AssertBalanced(List<string[]> segments, decimal expectedBpr02)
    {
        var bpr02 = decimal.Parse(segments.Single(s => s[0] == "BPR")[2]);
        Assert.Equal(expectedBpr02, bpr02);
        Assert.True(bpr02 >= 0m);
        var clp04 = segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4]));
        Assert.Equal(bpr02, clp04 - Plbs(segments).Sum(p => p.Amount));
    }

    private static EraPaymentInput PaymentInput(string tpId, decimal amount) => new()
    {
        TradingPartnerId = tpId,
        Payment = new Payment
        {
            CheckNumber = "0001000001",
            PaymentMethod = "ACH",
            TotalPaymentAmount = amount,
            PaymentDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            PayeeNPI = "1234567890",
            ClaimPayments = new List<ClaimPayment>
            {
                new() { ClaimId = "c-pay", PatientControlNumber = "CLM-PAY", ClaimStatusCode = "1", ChargeAmount = 1000m, PaymentAmount = amount },
            },
        },
    };

    [Fact]
    public void GenerateBatch_PureReversal_Bpr02Zero_NotificationOnly_BalanceForwardInPlb()
    {
        var inputs = new[] { ReversalInput("TP-A", -800m) };

        var segments = Segments(_generator.GenerateBatch(inputs, Partners("TP-A")).Single().EdiContent);

        var bpr = segments.Single(s => s[0] == "BPR");
        Assert.Equal(new[] { "BPR", "H", "0.00", "C", "NON" }, bpr.Take(5));
        Assert.Equal(17, bpr.Length);
        Assert.Contains(segments, s => s[0] == "CLP" && s[2] == "22" && s[4] == "-800.00");
        Assert.Equal(new[] { ("FB", "R-CHK001", -800m) }, Plbs(segments));
        AssertBalanced(segments, 0m);
    }

    [Fact]
    public void GenerateBatch_MixedNetNegative_Bpr02Zero_ForwardBalanceIsTheNet()
    {
        // A partner paid 500 and recouped 800 in the same 835: owes 300.
        var payment = PaymentInput("TP-A", 500m);
        var reversal = ReversalInput("TP-A", -800m);
        reversal.IsReversal = false; // same envelope, mixed

        var envelope = _generator.GenerateBatch(new[] { payment, reversal }, Partners("TP-A")).Single();
        var segments = Segments(envelope.EdiContent);

        Assert.Equal(0m, envelope.TotalPaymentAmount);
        Assert.Equal(-300m, envelope.ForwardBalanceAmount);
        Assert.Equal(new[] { ("FB", "0001000001", -300m) }, Plbs(segments));
        Assert.Equal("H", segments.Single(s => s[0] == "BPR")[1]);
        AssertBalanced(segments, 0m);
    }

    [Fact]
    public void GenerateBatch_MixedNetPositive_PaysTheNet_NoForwardBalance()
    {
        var payment = PaymentInput("TP-A", 1000m);
        var reversal = ReversalInput("TP-A", -800m);
        reversal.IsReversal = false;

        var envelope = _generator.GenerateBatch(new[] { payment, reversal }, Partners("TP-A")).Single();
        var segments = Segments(envelope.EdiContent);

        Assert.Equal(200m, envelope.TotalPaymentAmount);
        Assert.Equal(0m, envelope.ForwardBalanceAmount);
        Assert.Empty(Plbs(segments));
        Assert.Equal("C", segments.Single(s => s[0] == "BPR")[1]);
        AssertBalanced(segments, 200m);
    }

    [Fact]
    public void GenerateBatch_ReversalCarriesClp02_22InEdi()
    {
        // CLP02 is set upstream by ReversalRunService as "22"; the
        // generator emits whatever ClaimStatusCode is on the ClaimPayment.
        var inputs = new[] { ReversalInput("TP-A", -800m, clp02: "22") };

        var envelopes = _generator.GenerateBatch(inputs, Partners("TP-A"));

        Assert.Contains("CLP*CLM-001*22*", envelopes[0].EdiContent);
    }

    [Fact]
    public void GenerateBatch_ReversalCasUsesSignFlippedAmounts()
    {
        // Header CAS amounts are passed through from cp.ClaimAdjustments.
        // ReversalRunService sets these to the sign-flipped predecessor
        // CAS amounts; the generator emits them verbatim.
        var inputs = new[] { ReversalInput("TP-A", -800m) };

        var envelopes = _generator.GenerateBatch(inputs, Partners("TP-A"));

        Assert.Contains("CAS*PR*1*-200.00", envelopes[0].EdiContent);
    }

    [Fact]
    public void GenerateBatch_MixedInputs_FailsClosedToNonReversal()
    {
        // Mixed reversal + non-reversal in the same partner group should
        // not happen in practice — operators batch one or the other.
        // BatchEraGeneratorService's "envelope.IsReversal = inputs.All(IsReversal)"
        // makes this fail closed (the envelope persists as PaymentRun,
        // which is the safer default).
        var reversal = ReversalInput("TP-A", -100m);
        var paymentInput = new EraPaymentInput
        {
            TradingPartnerId = "TP-A",
            IsReversal = false,
            Payment = new Payment
            {
                CheckNumber = "PAY-001",
                PaymentMethod = "ACH",
                TotalPaymentAmount = 50m,
                PaymentDate = DateTime.UtcNow,
                PayerName = "CHO",
                PayeeName = "Provider",
                ClaimPayments = new List<ClaimPayment>
                {
                    new() { ClaimId = "c-pay", PatientControlNumber = "PAY", ClaimStatusCode = "1", ChargeAmount = 60m, PaymentAmount = 50m },
                },
            },
        };

        var envelopes = _generator.GenerateBatch(new[] { reversal, paymentInput }, Partners("TP-A"));

        Assert.Single(envelopes);
        Assert.False(envelopes[0].IsReversal);
    }
}
