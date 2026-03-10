using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

public class EraGeneratorServiceTests
{
    [Fact]
    public void Generate835_IncludesCoreSegments_AndSeCountMatches()
    {
        var service = new EraGeneratorService(NullLogger<EraGeneratorService>.Instance);

        var payment = new Payment
        {
            TenantId = "tenant-1",
            CheckNumber = "CHK12345",
            PaymentMethod = "ACH",
            TotalPaymentAmount = 150.00m,
            PaymentDate = new DateTime(2026, 3, 1),
            PayerName = "Test Payer",
            PayerId = "PAYER01",
            PayeeName = "Test Clinic",
            PayeeNPI = "1234567890",
            ClaimPayments =
            [
                new ClaimPayment
                {
                    ClaimId = "c1",
                    PatientControlNumber = "CLM-0001",
                    ClaimStatusCode = "1",
                    ChargeAmount = 200.00m,
                    PaymentAmount = 150.00m,
                    PatientResponsibilityAmount = 50.00m,
                    PayerClaimControlNumber = "PCN123",
                    MemberId = "MEM100",
                    RenderingProviderNPI = "1098765432",
                    ClaimReceivedDate = new DateTime(2026, 2, 28),
                    ClaimAdjustments =
                    [
                        new ClaimAdjustment
                        {
                            GroupCode = "CO",
                            ReasonCode = "45",
                            Amount = 50.00m
                        }
                    ],
                    ServiceLines =
                    [
                        new ServiceLinePayment
                        {
                            LineNumber = 1,
                            ProcedureCode = "99213",
                            ChargeAmount = 200.00m,
                            PaymentAmount = 150.00m,
                            Units = 1,
                            ServiceDateFrom = new DateTime(2026, 2, 27),
                            Adjustments =
                            [
                                new ServiceLineAdjustment
                                {
                                    GroupCode = "PR",
                                    ReasonCode = "1",
                                    Amount = 20.00m,
                                    RemarkCode = "N620"
                                }
                            ]
                        }
                    ]
                }
            ],
            ProviderAdjustments =
            [
                new ProviderAdjustment
                {
                    AdjustmentIdentifier = "FB",
                    ReferenceIdentification = "WITHHOLD",
                    Amount = 10.00m,
                    FiscalPeriodEnd = new DateTime(2026, 3, 31)
                }
            ]
        };

        var tp = new TradingPartnerInfo
        {
            InterchangeSenderId = "SENDERID",
            InterchangeReceiverId = "RECEIVERID",
            ApplicationSenderId = "APPSEND",
            ApplicationReceiverId = "APPRECV",
            PayerRoutingNumber = "011000015",
            PayerAccountNumber = "111122223333",
            PayeeRoutingNumber = "021000021",
            PayeeAccountNumber = "444455556666"
        };

        var edi = service.Generate835(payment, tp);

        Assert.Contains("ST*835*0001*005010X221A1~", edi);
        Assert.Contains("BPR*C*150.00*C*ACH*CCP*01*011000015*DA*111122223333*20260301*01*021000021*DA*444455556666*20260301~", edi);
        Assert.Contains("TRN*1*CHK12345*PAYER01~", edi);
        Assert.Contains("N1*PR*Test Payer*XV*PAYER01~", edi);
        Assert.Contains("N1*PE*Test Clinic*XX*1234567890~", edi);
        Assert.Contains("CLP*CLM-0001*1*200.00*150.00*50.00*HM*PCN123~", edi);
        Assert.Contains("SVC*HC:99213*200.00*150.00**1~", edi);
        Assert.Contains("CAS*CO*45*50.00~", edi);
        Assert.Contains("CAS*PR*1*20.00*N620~", edi);
        Assert.Contains("PLB*1234567890*20260331*FB:WITHHOLD*10.00~", edi);

        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var stIndex = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var seIndex = Array.FindIndex(segments, s => s.StartsWith("SE*"));

        Assert.True(stIndex >= 0 && seIndex > stIndex, "ST/SE segments were not found in expected order.");

        var seParts = segments[seIndex].Split('*');
        var declaredSeCount = int.Parse(seParts[1]);
        var actualSeCount = seIndex - stIndex + 1;

        Assert.Equal(actualSeCount, declaredSeCount);
    }
}
