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
            OriginatingCompanyId = "1123456789",
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
        payment.ClaimPayments[0].PaymentAmount = 0m;              // balanced: CLP04 = BPR02
        payment.ClaimPayments[0].ServiceLines[0].PaymentAmount = 0m;
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // A zero-pay 835 moves no money: NON, whatever the payment method.
        Assert.Contains("BPR*I*0.00*C*NON", era);
    }

    [Fact]
    public void Generate835_ContainsTRNWithCheckNumber()
    {
        var payment = CreateTestPayment();
        var tp = CreateTestTradingPartner();

        var era = _generator.Generate835(payment, tp);

        // TRN03 = originating company id, identical to BPR10
        Assert.Contains("TRN*1*0001000001*1123456789~", era);
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

    // ═══════════════════════════════════════════════════════════════════
    // BPR ELEMENT POSITIONS (005010X221A1)
    // ═══════════════════════════════════════════════════════════════════

    private static string[] SegmentElements(string era, string segmentId) =>
        era.Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Single(s => s.StartsWith(segmentId + "*", StringComparison.Ordinal))
            .Split('*');

    [Fact]
    public void Generate835_AchBpr_EveryElementAtItsX12Position()
    {
        var tp = CreateTestTradingPartner();
        tp.OriginatingCompanySupplementalCode = "SUPP00001";

        var bpr = SegmentElements(_generator.Generate835(CreateTestPayment(), tp), "BPR");

        Assert.Equal(17, bpr.Length);           // BPR + BPR01..BPR16
        Assert.Equal("BPR", bpr[0]);
        Assert.Equal("C", bpr[1]);              // handling code
        Assert.Equal("1250.00", bpr[2]);        // amount
        Assert.Equal("C", bpr[3]);              // credit
        Assert.Equal("ACH", bpr[4]);            // payment method
        Assert.Equal("CCP", bpr[5]);            // payment format
        Assert.Equal("01", bpr[6]);             // sender DFI qualifier
        Assert.Equal("021000021", bpr[7]);      // sender DFI
        Assert.Equal("DA", bpr[8]);             // sender account qualifier
        Assert.Equal("123456789", bpr[9]);      // sender account
        Assert.Equal("1123456789", bpr[10]);    // originating company id
        Assert.Equal("SUPP00001", bpr[11]);     // originating company supplemental code
        Assert.Equal("01", bpr[12]);            // receiver DFI qualifier
        Assert.Equal("021000089", bpr[13]);     // receiver DFI
        Assert.Equal("DA", bpr[14]);            // receiver account qualifier
        Assert.Equal("987654321", bpr[15]);     // receiver account
        Assert.Equal("20260315", bpr[16]);      // EFT effective date
    }

    [Fact]
    public void Generate835_AchBpr_WithoutSupplementalCode_LeavesBpr11Empty()
    {
        var bpr = SegmentElements(_generator.Generate835(CreateTestPayment(), CreateTestTradingPartner()), "BPR");

        Assert.Equal(17, bpr.Length);
        Assert.Equal("1123456789", bpr[10]);
        Assert.Equal(string.Empty, bpr[11]);
        Assert.Equal("01", bpr[12]);
        Assert.Equal("20260315", bpr[16]);
    }

    [Theory]
    [InlineData("CHK", "CHK")]
    [InlineData("NON", "NON")]
    [InlineData("Check", "CHK")]
    [InlineData("check", "CHK")]
    [InlineData("Wire", "NON")]
    public void Generate835_NonAchBpr_EmptyBpr05To15_DateInBpr16(string method, string expectedBpr04)
    {
        var payment = CreateTestPayment();
        payment.PaymentMethod = method;

        var bpr = SegmentElements(_generator.Generate835(payment, CreateTestTradingPartner()), "BPR");

        Assert.Equal(17, bpr.Length);
        Assert.Equal(expectedBpr04, bpr[4]);
        for (var i = 5; i <= 15; i++)
            Assert.Equal(string.Empty, bpr[i]);
        Assert.Equal("20260315", bpr[16]);
    }

    [Fact]
    public void Generate835_Trn03_IsIdenticalToBpr10()
    {
        var era = _generator.Generate835(CreateTestPayment(), CreateTestTradingPartner());

        var bpr = SegmentElements(era, "BPR");
        var trn = SegmentElements(era, "TRN");
        Assert.Equal("0001000001", trn[2]);
        Assert.Equal(bpr[10], trn[3]);
    }

    [Fact]
    public void Generate835_CheckWithoutOriginatingCompanyId_Throws_NoSynthesisedTrn03()
    {
        // TRN03 is required for every payment method; it is never made up
        // from the payer id (previously "BCBS001"/"1999999999").
        var payment = CreateTestPayment();
        payment.PaymentMethod = "CHK";
        var tp = CreateTestTradingPartner();
        tp.OriginatingCompanyId = null;

        var ex = Assert.Throws<InvalidOperationException>(() => _generator.Generate835(payment, tp));
        Assert.Contains("TRN03", ex.Message);
    }

    [Fact]
    public void Generate835_CheckWithOriginatingCompanyId_Trn03IsTheConfiguredId()
    {
        var payment = CreateTestPayment();
        payment.PaymentMethod = "Check";

        var era = _generator.Generate835(payment, CreateTestTradingPartner());

        Assert.Equal("1123456789", SegmentElements(era, "TRN")[3]);
        Assert.Equal("CHK", SegmentElements(era, "BPR")[4]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BALANCING: BPR02 = sum(CLP04) - sum(PLB); sum(SVC03) = CLP04
    // ═══════════════════════════════════════════════════════════════════

    private static void AssertBalanced(string era)
    {
        var segments = era.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();
        var bpr02 = decimal.Parse(segments.Single(s => s[0] == "BPR")[2]);
        var clp04 = segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4]));
        // PLB amounts are the odd elements from PLB04 on (reason:ref, amount pairs).
        var plb = segments.Where(s => s[0] == "PLB")
            .Sum(s => s.Skip(3).Where((_, i) => i % 2 == 1).Sum(decimal.Parse));
        Assert.Equal(bpr02, clp04 - plb);

        // Each CLP's SVC03 total equals its CLP04.
        decimal? currentClp = null;
        decimal svcTotal = 0;
        var svcCount = 0;
        foreach (var s in segments.Append(new[] { "END" }))
        {
            if (s[0] is "CLP" or "PLB" or "SE" or "END")
            {
                if (currentClp.HasValue && svcCount > 0)
                    Assert.Equal(currentClp.Value, svcTotal);
                currentClp = s[0] == "CLP" ? decimal.Parse(s[4]) : null;
                svcTotal = 0;
                svcCount = 0;
            }
            else if (s[0] == "SVC" && currentClp.HasValue)
            {
                svcTotal += decimal.Parse(s[3]);
                svcCount++;
            }
        }
    }

    [Fact]
    public void Generate835_WithProviderAdjustment_Balances_BprEqualsClpLessPlb()
    {
        var payment = CreateTestPayment();
        payment.ProviderAdjustments.Add(new ProviderAdjustment
        {
            AdjustmentIdentifier = "FB", ReferenceIdentification = "WITHHOLD", Amount = 50.00m,
            FiscalPeriodEnd = new DateTime(2026, 3, 31)
        });
        payment.TotalPaymentAmount = 1200.00m;   // 1250 CLP04 - 50 PLB

        var era = _generator.Generate835(payment, CreateTestTradingPartner());

        AssertBalanced(era);
    }

    [Fact]
    public void Generate835_BprNotEqualToClpLessPlb_Throws()
    {
        var payment = CreateTestPayment();
        payment.ProviderAdjustments.Add(new ProviderAdjustment { AdjustmentIdentifier = "FB", Amount = 50.00m });
        // TotalPaymentAmount left at 1250: does not account for the PLB.

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _generator.Generate835(payment, CreateTestTradingPartner()));
        Assert.Contains("BPR02", ex.Message);
    }

    [Fact]
    public void Generate835_ServiceLinesNotSummingToClp04_Throws()
    {
        var payment = CreateTestPayment();
        payment.ClaimPayments[0].ServiceLines[0].PaymentAmount = 1500.00m; // the billed charge

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _generator.Generate835(payment, CreateTestTradingPartner()));
        Assert.Contains("SVC03", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456789")]     // 9 characters
    [InlineData("12345678901")]   // 11 characters
    public void Generate835_AchWithoutValidOriginatingCompanyId_Throws(string? originatingCompanyId)
    {
        var tp = CreateTestTradingPartner();
        tp.OriginatingCompanyId = originatingCompanyId;

        var ex = Assert.Throws<InvalidOperationException>(() => _generator.Generate835(CreateTestPayment(), tp));
        Assert.Contains("BPR10", ex.Message);
    }

    [Fact]
    public void Generate835_AchWithoutPayeeAccount_Throws()
    {
        var tp = CreateTestTradingPartner();
        tp.PayeeAccountNumber = null;

        var ex = Assert.Throws<InvalidOperationException>(() => _generator.Generate835(CreateTestPayment(), tp));
        Assert.Contains("BPR15", ex.Message);
    }

    [Fact]
    public void Generate835_AchBpr_StillMaskedForDownload()
    {
        var era = EdiBankNumberMasking.MaskBpr(_generator.Generate835(CreateTestPayment(), CreateTestTradingPartner()));
        var bpr = SegmentElements(era, "BPR");

        Assert.Equal("XXXXX0021", bpr[7]);
        Assert.Equal("XXXXX6789", bpr[9]);
        Assert.Equal("1123456789", bpr[10]); // originating company id is not a bank number
        Assert.Equal("XXXXX0089", bpr[13]);
        Assert.Equal("XXXXX4321", bpr[15]);
    }
}
