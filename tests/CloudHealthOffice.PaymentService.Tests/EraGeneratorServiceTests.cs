using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

public class EraGeneratorServiceTests
{
    private readonly EraGeneratorService _generator;

    public EraGeneratorServiceTests()
    {
        _generator = new EraGeneratorService(NullLogger<EraGeneratorService>.Instance);
    }

    private static Payment CreateTestPayment()
    {
        return new Payment
        {
            Id = "pay-era-test",
            CheckNumber = "0001000001",
            PaymentMethod = "ACH",
            TotalPaymentAmount = 1250.00m,
            PaymentDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Blue Cross",
            PayerId = "BCBS001",
            PayeeName = "Springfield Medical",
            PayeeNPI = "1234567890",
            ClaimPayments = new List<ClaimPayment>
            {
                new()
                {
                    ClaimId = "claim-001",
                    PatientControlNumber = "CLM-001",
                    ClaimStatusCode = "1",
                    ChargeAmount = 1500.00m,
                    PaymentAmount = 1250.00m,
                    PatientResponsibilityAmount = 250.00m,
                    PayerClaimControlNumber = "ICN-001",
                    MemberId = "MEM-001",
                    RenderingProviderNPI = "9876543210",
                    ServiceLines = new List<ServiceLinePayment>
                    {
                        new()
                        {
                            LineNumber = 1,
                            ProcedureCode = "99213",
                            ChargeAmount = 1500.00m,
                            PaymentAmount = 1250.00m,
                            Units = 1
                        }
                    }
                }
            }
        };
    }

    private static TradingPartnerInfo CreateTestTradingPartner()
    {
        return new TradingPartnerInfo
        {
            InterchangeSenderId = "TESTSENDER",
            InterchangeReceiverId = "TESTRECEIVER",
            ApplicationSenderId = "APPSENDER",
            ApplicationReceiverId = "APPRECEIVER",
            PayerRoutingNumber = "021000021",
            PayerAccountNumber = "123456789",
            PayeeRoutingNumber = "021000089",
            PayeeAccountNumber = "987654321"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // X12 835 STRUCTURE VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Generate835_ContainsRequiredEnvelopeSegments()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // ISA/IEA envelope
        Assert.StartsWith("ISA*", era);
        Assert.Contains("IEA*", era);

        // GS/GE functional group
        Assert.Contains("GS*HP*", era);
        Assert.Contains("GE*1*1~", era);

        // ST/SE transaction set
        Assert.Contains("ST*835*0001*005010X221A1~", era);
        Assert.Contains("SE*", era);
    }

    [Fact]
    public void Generate835_ContainsBPRFinancialInformation()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // BPR with payment amount
        Assert.Contains("BPR*C*1250.00*C*ACH", era);
    }

    [Fact]
    public void Generate835_ZeroPayment_UsesBPRCodeI()
    {
        var payment = CreateTestPayment();
        payment.TotalPaymentAmount = 0m;
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("BPR*I*0.00*C*ACH", era);
    }

    [Fact]
    public void Generate835_ContainsTRNWithCheckNumber()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("TRN*1*0001000001*BCBS001~", era);
    }

    [Fact]
    public void Generate835_ContainsPayerAndPayeeIdentification()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // 1000A payer
        Assert.Contains("N1*PR*Blue Cross*XV*BCBS001~", era);
        // 1000B payee with NPI
        Assert.Contains("N1*PE*Springfield Medical*XX*1234567890~", era);
    }

    [Fact]
    public void Generate835_ContainsCLPClaimLoop()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // CLP segment with claim data
        Assert.Contains("CLP*CLM-001*1*1500.00*1250.00*250.00*HM*ICN-001~", era);
    }

    [Fact]
    public void Generate835_ContainsSVCServiceLineLoop()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // SVC segment with procedure code
        Assert.Contains("SVC*HC:99213*1500.00*1250.00**1~", era);
    }

    [Fact]
    public void Generate835_ContainsPatientNM1WhenMemberIdPresent()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("NM1*QC*1**MEM-001****MI*MEM-001~", era);
    }

    [Fact]
    public void Generate835_ContainsRenderingProviderNM1()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("NM1*82*1*****XX*9876543210~", era);
    }

    [Fact]
    public void Generate835_UsesSegmentTerminator()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // Every segment ends with ~
        var segments = era.Split('~', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(segments.Length >= 10, "Expected at least 10 segments in 835");
    }

    [Fact]
    public void Generate835_SESegmentCountMatchesActualSegments()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // Extract SE segment count
        var segments = era.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var seSegment = segments.First(s => s.StartsWith("SE*"));
        var seCount = int.Parse(seSegment.Split('*')[1]);

        // Count segments between ST and SE (inclusive)
        var stIndex = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var seIndex = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        var actualCount = seIndex - stIndex + 1;

        Assert.Equal(actualCount, seCount);
    }

    [Fact]
    public void Generate835_AchPaymentMethod_UsesBankingDetails()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // ACH with routing/account numbers
        Assert.Contains("021000021", era);
        Assert.Contains("123456789", era);
    }

    [Fact]
    public void Generate835_CheckPaymentMethod_CHK()
    {
        var payment = CreateTestPayment();
        payment.PaymentMethod = "CHK";
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("BPR*C*1250.00*C*CHK", era);
    }

    [Fact]
    public void Generate835_MultipleClaims_GeneratesMultipleCLPLoops()
    {
        var payment = CreateTestPayment();
        payment.ClaimPayments.Add(new ClaimPayment
        {
            ClaimId = "claim-002",
            PatientControlNumber = "CLM-002",
            ClaimStatusCode = "1",
            ChargeAmount = 500.00m,
            PaymentAmount = 400.00m,
            PatientResponsibilityAmount = 100.00m
        });
        payment.TotalPaymentAmount = 1650.00m;
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("CLP*CLM-001*", era);
        Assert.Contains("CLP*CLM-002*", era);
    }

    [Fact]
    public void Generate835_WithClaimAdjustments_GeneratesCASSegments()
    {
        var payment = CreateTestPayment();
        payment.ClaimPayments[0].ClaimAdjustments = new List<ClaimAdjustment>
        {
            new() { GroupCode = "CO", ReasonCode = "45", Amount = 250.00m }
        };
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        Assert.Contains("CAS*CO*45*250.00~", era);
    }

    // ── BPR element positions (005010X221A1) ─────────────────────────────
    // The pre-existing assertions stopped at BPR04, which is why an
    // off-by-one from BPR10 onward went unnoticed: the EFT effective date
    // was landing in BPR10 (Originating Company Identifier), pushing the
    // receiver DFI qualifier, routing number, account qualifier and account
    // number each one element to the right and dropping BPR16 entirely.

    [Fact]
    public void Generate835_AchBpr_PlacesEveryElementAtItsSpecifiedPosition()
    {
        var era = _generator.Generate835(CreateTestPayment(), CreateTestTradingPartner());

        var bpr = era.Split('~').First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal));
        var el = bpr.Split('*');

        Assert.Equal("BPR", el[0]);
        Assert.Equal("C", el[1]);                    // BPR01 transaction handling
        Assert.Equal("1250.00", el[2]);              // BPR02 amount
        Assert.Equal("C", el[3]);                    // BPR03 credit
        Assert.Equal("ACH", el[4]);                  // BPR04 payment method
        Assert.Equal("CCP", el[5]);                  // BPR05 payment format
        Assert.Equal("01", el[6]);                   // BPR06 sender DFI qualifier
        Assert.Equal("021000021", el[7]);            // BPR07 sender routing
        Assert.Equal("DA", el[8]);                   // BPR08 sender acct qualifier
        Assert.Equal("123456789", el[9]);            // BPR09 sender account
        Assert.Equal("BCBS001", el[10]);             // BPR10 originating company id
        Assert.Equal(string.Empty, el[11]);          // BPR11 not used
        Assert.Equal("01", el[12]);                  // BPR12 receiver DFI qualifier
        Assert.Equal("021000089", el[13]);           // BPR13 receiver routing
        Assert.Equal("DA", el[14]);                  // BPR14 receiver acct qualifier
        Assert.Equal("987654321", el[15]);           // BPR15 receiver account
        Assert.Equal("20260315", el[16]);            // BPR16 EFT effective date
        Assert.Equal(17, el.Length);
    }

    [Fact]
    public void Generate835_AchBpr_DoesNotPutADateInTheOriginatingCompanyIdentifier()
    {
        var era = _generator.Generate835(CreateTestPayment(), CreateTestTradingPartner());

        var bpr = era.Split('~').First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal));
        var bpr10 = bpr.Split('*')[10];

        Assert.False(
            bpr10.Length == 8 && bpr10.All(char.IsDigit),
            $"BPR10 must be the originating company identifier, not a date. Got '{bpr10}'.");
    }

    [Fact]
    public void Generate835_CheckBpr_CarriesIssueDateInBpr16AndLeavesBankFieldsEmpty()
    {
        var payment = CreateTestPayment();
        payment.PaymentMethod = "CHK";

        var era = _generator.Generate835(payment, CreateTestTradingPartner());

        var el = era.Split('~')
            .First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal))
            .Split('*');

        Assert.Equal("CHK", el[4]);
        foreach (var i in Enumerable.Range(5, 11))   // BPR05..BPR15
            Assert.Equal(string.Empty, el[i]);
        Assert.Equal("20260315", el[16]);            // BPR16 issue date
    }

    [Fact]
    public void Generate835_NonPayment_EmitsNoFinancialDetail()
    {
        var payment = CreateTestPayment();
        payment.PaymentMethod = "NON";

        var era = _generator.Generate835(payment, CreateTestTradingPartner());

        var bpr = era.Split('~').First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal));

        Assert.Equal("BPR*I*1250.00*C*NON", bpr);
    }

    // ── Payer identity is required, never defaulted ──────────────────────

    [Fact]
    public void Generate835_MissingPayerId_ThrowsRatherThanEmittingAPlaceholder()
    {
        var payment = CreateTestPayment();
        payment.PayerId = null;

        var ex = Assert.Throws<InvalidOperationException>(
            () => _generator.Generate835(payment, CreateTestTradingPartner()));

        Assert.Contains("PayerId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate835_MissingCheckNumber_ThrowsBecauseTrn02IsTheReassociationKey()
    {
        var payment = CreateTestPayment();
        payment.CheckNumber = string.Empty;

        Assert.Throws<InvalidOperationException>(
            () => _generator.Generate835(payment, CreateTestTradingPartner()));
    }

    [Fact]
    public void Generate835_PayerIdentity_IsConsistentAcrossBpr10Trn03AndN1Pr()
    {
        var era = _generator.Generate835(CreateTestPayment(), CreateTestTradingPartner());

        var bpr10 = era.Split('~')
            .First(seg => seg.StartsWith("BPR*", StringComparison.Ordinal)).Split('*')[10];
        var trn03 = era.Split('~')
            .First(seg => seg.StartsWith("TRN*", StringComparison.Ordinal)).Split('*')[3];
        var n1pr = era.Split('~')
            .First(seg => seg.StartsWith("N1*PR*", StringComparison.Ordinal)).Split('*')[4];

        Assert.Equal("BCBS001", bpr10);
        Assert.Equal("BCBS001", trn03);
        Assert.Equal("BCBS001", n1pr);
    }
}
