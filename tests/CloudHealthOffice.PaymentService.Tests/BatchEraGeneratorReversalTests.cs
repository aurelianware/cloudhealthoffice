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
        Assert.Equal(-800m, envelopes[0].TotalPaymentAmount);
    }

    [Fact]
    public void GenerateBatch_NegativeBpr_EmitsInformationalCode()
    {
        // Per existing line 176 logic in BatchEraGeneratorService, BPR01
        // branches on amount sign: positive → "C" (Credit), zero/negative
        // → "I" (Informational). Reversal envelopes naturally sit on the
        // "I" branch.
        var inputs = new[] { ReversalInput("TP-A", -800m) };

        var envelopes = _generator.GenerateBatch(inputs, Partners("TP-A"));

        Assert.Contains("BPR*I*-800.00", envelopes[0].EdiContent);
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
