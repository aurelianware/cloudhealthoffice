using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

public class BatchEraGeneratorServiceTests
{
    private readonly BatchEraGeneratorService _generator =
        new(NullLogger<BatchEraGeneratorService>.Instance);

    private static Payment Pay(string checkNumber, string claimId, decimal amount, string? payerId = "BCBS001")
    {
        return new Payment
        {
            Id = "pay-" + claimId,
            CheckNumber = checkNumber,
            PaymentMethod = "ACH",
            TotalPaymentAmount = amount,
            PaymentDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Cloud Health Office",
            PayerId = payerId,
            PayeeName = "Acme Medical",
            PayeeNPI = "1234567890",
            ClaimPayments = new List<ClaimPayment>
            {
                new()
                {
                    ClaimId = claimId,
                    PatientControlNumber = "CLM-" + claimId,
                    ClaimStatusCode = "1",
                    ChargeAmount = amount + 100m,
                    PaymentAmount = amount,
                    PatientResponsibilityAmount = 100m,
                    MemberId = "MEM-" + claimId,
                    ServiceLines = new List<ServiceLinePayment>
                    {
                        new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = amount + 100m, PaymentAmount = amount, Units = 1 }
                    }
                }
            }
        };
    }

    private static TradingPartnerInfo TpAch(string partnerId) => new()
    {
        InterchangeSenderId = $"S{partnerId}",
        InterchangeReceiverId = $"R{partnerId}",
        ApplicationSenderId = "APPSENDER",
        ApplicationReceiverId = "APPRECEIVER",
        PayerRoutingNumber = "021000021",
        PayerAccountNumber = "111",
        PayeeRoutingNumber = "021000089",
        PayeeAccountNumber = "222"
    };

    [Fact]
    public void GenerateBatch_SinglePartner_ProducesOneEnvelope()
    {
        var inputs = new[]
        {
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) },
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0002", "c2", 750m) }
        };

        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelopes = _generator.GenerateBatch(inputs, partners);

        var envelope = Assert.Single(envelopes);
        Assert.Equal("TP-A", envelope.TradingPartnerId);
        Assert.Equal(2, envelope.ClaimCount);
        Assert.Equal(1250m, envelope.TotalPaymentAmount);
        Assert.Equal(2, envelope.ClaimIds.Count);
        Assert.Contains("c1", envelope.ClaimIds);
        Assert.Contains("c2", envelope.ClaimIds);
    }

    [Fact]
    public void GenerateBatch_MultiplePartners_OneEnvelopePerPartner()
    {
        var inputs = new[]
        {
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) },
            new EraPaymentInput { TradingPartnerId = "TP-B", Payment = Pay("0002", "c2", 750m) },
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0003", "c3", 200m) }
        };

        var partners = new Dictionary<string, TradingPartnerInfo>
        {
            ["TP-A"] = TpAch("A"),
            ["TP-B"] = TpAch("B")
        };

        var envelopes = _generator.GenerateBatch(inputs, partners);

        Assert.Equal(2, envelopes.Count);
        var envA = envelopes.Single(e => e.TradingPartnerId == "TP-A");
        var envB = envelopes.Single(e => e.TradingPartnerId == "TP-B");
        Assert.Equal(700m, envA.TotalPaymentAmount);
        Assert.Equal(750m, envB.TotalPaymentAmount);
    }

    [Fact]
    public void GenerateBatch_UnresolvedPartner_SkipsWithWarning()
    {
        var inputs = new[]
        {
            new EraPaymentInput { TradingPartnerId = "TP-MISSING", Payment = Pay("0001", "c1", 500m) }
        };

        var partners = new Dictionary<string, TradingPartnerInfo>(); // empty

        var envelopes = _generator.GenerateBatch(inputs, partners);

        Assert.Empty(envelopes);
    }

    [Fact]
    public void GenerateBatch_EmptyTradingPartnerId_Skipped()
    {
        var inputs = new[]
        {
            new EraPaymentInput { TradingPartnerId = "", Payment = Pay("0001", "c1", 500m) }
        };

        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelopes = _generator.GenerateBatch(inputs, partners);

        Assert.Empty(envelopes);
    }

    [Fact]
    public void GenerateBatch_EnvelopeContainsRequiredEnvelopeSegments()
    {
        var inputs = new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) } };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelope = _generator.GenerateBatch(inputs, partners).Single();

        Assert.StartsWith("ISA*", envelope.EdiContent);
        Assert.Contains("GS*HP*", envelope.EdiContent);
        Assert.Contains("ST*835*0001*005010X221A1~", envelope.EdiContent);
        Assert.Contains("BPR*", envelope.EdiContent);
        Assert.Contains("TRN*1*", envelope.EdiContent);
        Assert.Contains("N1*PR*", envelope.EdiContent);
        Assert.Contains("N1*PE*", envelope.EdiContent);
        Assert.Contains("CLP*CLM-c1*", envelope.EdiContent);
        Assert.Contains("SE*", envelope.EdiContent);
        Assert.Contains("GE*1*1~", envelope.EdiContent);
        Assert.Contains("IEA*1*", envelope.EdiContent);
    }

    [Fact]
    public void GenerateBatch_SE01CountMatchesActualSegments()
    {
        var inputs = new[]
        {
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) },
            new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0002", "c2", 250m) }
        };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelope = _generator.GenerateBatch(inputs, partners).Single();
        var segments = envelope.EdiContent.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var seSegment = segments.First(s => s.StartsWith("SE*"));
        var seCount = int.Parse(seSegment.Split('*')[1]);

        var stIndex = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var seIndex = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        var actualCount = seIndex - stIndex + 1;

        Assert.Equal(actualCount, seCount);
    }

    [Fact]
    public void GenerateBatch_DeniedClaim_EmitsClaimAdjustmentCAS()
    {
        var pay = Pay("0001", "c1", 0m);
        pay.ClaimPayments[0].ClaimAdjustments.Add(
            new ClaimAdjustment { GroupCode = "CO", ReasonCode = "29", Amount = 0m, ReasonDescription = "Late filing" });
        pay.ClaimPayments[0].ClaimStatusCode = "3";

        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };
        var envelope = _generator.GenerateBatch(
            new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = pay } },
            partners).Single();

        Assert.Contains("CAS*CO*29*0.00~", envelope.EdiContent);
        Assert.Contains("BPR*I*0.00*C*ACH", envelope.EdiContent);
    }

    [Fact]
    public void GenerateBatch_PartialPayment_EmitsLineLevelCASWithRemark()
    {
        var pay = Pay("0001", "c1", 400m);
        pay.ClaimPayments[0].ServiceLines[0].Adjustments.Add(
            new ServiceLineAdjustment
            {
                GroupCode = "CO",
                ReasonCode = "236",
                Amount = 100m,
                RemarkCode = "M86"
            });

        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };
        var envelope = _generator.GenerateBatch(
            new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = pay } },
            partners).Single();

        Assert.Contains("CAS*CO*236*100.00*M86~", envelope.EdiContent);
    }

    [Fact]
    public void GenerateBatch_MixedDenialAndPaid_EmitsAllClaimsInEnvelope()
    {
        var paid = Pay("0001", "c-paid", 750m);
        var denied = Pay("0002", "c-denied", 0m);
        denied.ClaimPayments[0].ClaimStatusCode = "3";
        denied.ClaimPayments[0].ClaimAdjustments.Add(
            new ClaimAdjustment { GroupCode = "CO", ReasonCode = "29", Amount = 0m });

        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };
        var envelope = _generator.GenerateBatch(
            new[]
            {
                new EraPaymentInput { TradingPartnerId = "TP-A", Payment = paid },
                new EraPaymentInput { TradingPartnerId = "TP-A", Payment = denied }
            },
            partners).Single();

        Assert.Equal(2, envelope.ClaimCount);
        Assert.Contains("CLM-c-paid", envelope.EdiContent);
        Assert.Contains("CLM-c-denied", envelope.EdiContent);
    }

    [Fact]
    public void GenerateBatch_NullInputs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _generator.GenerateBatch(null!, new Dictionary<string, TradingPartnerInfo>()));
    }

    [Fact]
    public void GenerateBatch_NullPartners_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _generator.GenerateBatch(Array.Empty<EraPaymentInput>(), null!));
    }

    // ── BPR element positions (005010X221A1) ─────────────────────────────
    // BatchEraGeneratorService is the production path: PaymentRunService and
    // ReversalRunService both generate through GenerateBatch, not through
    // EraGeneratorService.Generate835. These assertions pin each BPR element
    // to its specified position so a shift cannot reach live 835s unnoticed.

    [Fact]
    public void GenerateBatch_AchBpr_PlacesEveryElementAtItsSpecifiedPosition()
    {
        var inputs = new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) } };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelope = Assert.Single(_generator.GenerateBatch(inputs, partners));
        var el = envelope.EdiContent.Split('~')
            .First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal)).Split('*');

        Assert.Equal("C", el[1]);              // BPR01 transaction handling
        Assert.Equal("500.00", el[2]);         // BPR02 amount
        Assert.Equal("C", el[3]);              // BPR03 credit
        Assert.Equal("ACH", el[4]);            // BPR04 payment method
        Assert.Equal("CCP", el[5]);            // BPR05 payment format
        Assert.Equal("01", el[6]);             // BPR06 sender DFI qualifier
        Assert.Equal("021000021", el[7]);      // BPR07 sender routing
        Assert.Equal("DA", el[8]);             // BPR08 sender acct qualifier
        Assert.Equal("111", el[9]);            // BPR09 sender account
        Assert.Equal("BCBS001", el[10]);       // BPR10 originating company id
        Assert.Equal(string.Empty, el[11]);    // BPR11 not used
        Assert.Equal("01", el[12]);            // BPR12 receiver DFI qualifier
        Assert.Equal("021000089", el[13]);     // BPR13 receiver routing
        Assert.Equal("DA", el[14]);            // BPR14 receiver acct qualifier
        Assert.Equal("222", el[15]);           // BPR15 receiver account
        Assert.Equal("20260501", el[16]);      // BPR16 EFT effective date
        Assert.Equal(17, el.Length);
    }

    [Fact]
    public void GenerateBatch_AchBpr_DoesNotPutADateInTheOriginatingCompanyIdentifier()
    {
        var inputs = new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) } };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var envelope = Assert.Single(_generator.GenerateBatch(inputs, partners));
        var bpr10 = envelope.EdiContent.Split('~')
            .First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal)).Split('*')[10];

        Assert.False(
            bpr10.Length == 8 && bpr10.All(char.IsDigit),
            $"BPR10 must be the originating company identifier, not a date. Got '{bpr10}'.");
    }

    [Fact]
    public void GenerateBatch_PayerIdentity_IsConsistentAcrossBpr10Trn03AndN1Pr()
    {
        var inputs = new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m) } };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var segments = Assert.Single(_generator.GenerateBatch(inputs, partners)).EdiContent.Split('~');

        Assert.Equal("BCBS001",
            segments.First(s => s.StartsWith("BPR*", StringComparison.Ordinal)).Split('*')[10]);
        Assert.Equal("BCBS001",
            segments.First(s => s.StartsWith("TRN*", StringComparison.Ordinal)).Split('*')[3]);
        Assert.Equal("BCBS001",
            segments.First(s => s.StartsWith("N1*PR*", StringComparison.Ordinal)).Split('*')[4]);
    }

    [Fact]
    public void GenerateBatch_MissingPayerId_ThrowsRatherThanEmittingAPlaceholder()
    {
        var inputs = new[] { new EraPaymentInput { TradingPartnerId = "TP-A", Payment = Pay("0001", "c1", 500m, payerId: null) } };
        var partners = new Dictionary<string, TradingPartnerInfo> { ["TP-A"] = TpAch("A") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => _generator.GenerateBatch(inputs, partners));

        Assert.Contains("PayerId", ex.Message, StringComparison.Ordinal);
    }
}
