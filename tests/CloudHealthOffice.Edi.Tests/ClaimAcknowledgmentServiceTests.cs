using ClaimsService.Models;
using ClaimsService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

public class ClaimAcknowledgmentServiceTests
{
    [Theory]
    [InlineData(ClaimStatus.Submitted, "A:20:85", "WQ")]
    [InlineData(ClaimStatus.InAdjudication, "P:15:85", "WQ")]
    [InlineData(ClaimStatus.Denied, "F:4:85", "U")]
    [InlineData(ClaimStatus.Paid, "F:2:85", "U")]
    [InlineData(ClaimStatus.Voided, "R:19:85", "U")]
    public void Generate277CA_MapsStatusToExpectedStcAndAction(
        ClaimStatus status,
        string expectedStc,
        string expectedAction)
    {
        var service = new ClaimAcknowledgmentService(NullLogger<ClaimAcknowledgmentService>.Instance);

        var submittedDate = new DateTime(2026, 3, 1);
        var claim = new Claim
        {
            TenantId = "tenant-1",
            Id = "abcdef123456",
            ClaimNumber = "CLM-123",
            MemberId = "MEM001",
            LineOfBusiness = LineOfBusiness.Commercial,
            BillingProviderNPI = "1234567890",
            BillingProviderName = "Provider*Name",
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 123.45m,
            ServiceDateFrom = new DateTime(2026, 2, 28),
            ServiceDateTo = new DateTime(2026, 2, 28),
            SubscriberFirstName = "Jane",
            SubscriberLastName = "Doe",
            Status = status,
            SubmittedDate = submittedDate,
        };

        var cfg = new ClaimAcknowledgmentConfig
        {
            InterchangeSenderId = "CHO",
            InterchangeReceiverId = "RCVR",
            ApplicationSenderId = "CHOAPP",
            ApplicationReceiverId = "RCVRAPP",
            PayerName = "Payer*Name",
            PayerId = "PAY01",
            PayerOriginatorId = "ORIG01"
        };

        var edi = service.Generate277CA(claim, cfg);

        Assert.Contains("ST*277*0001*005010X214~", edi);
        Assert.Contains("GS*HN*CHOAPP*RCVRAPP*", edi);
        Assert.Contains("NM1*PR*2*Payer Name*****PI*PAY01~", edi);
        Assert.Contains("NM1*41*2*Provider Name*****46*1234567890~", edi);
        Assert.Contains("TRN*1*CLM-123*ORIG01~", edi);
        Assert.Contains("TRN*2*CLM-123*ORIG01~", edi);
        Assert.Contains($"STC*{expectedStc}*{submittedDate:yyyyMMdd}*{expectedAction}*123.45~", edi);
        Assert.Contains("DTP*472*D8*20260228~", edi);
    }

    [Fact]
    public void Generate277CA_IncludesPayerClaimControlRef_WhenPresent()
    {
        var service = new ClaimAcknowledgmentService(NullLogger<ClaimAcknowledgmentService>.Instance);

        var claim = new Claim
        {
            TenantId = "tenant-1",
            Id = "xyz987654321",
            ClaimNumber = "CLM-456",
            MemberId = "MEM002",
            LineOfBusiness = LineOfBusiness.Commercial,
            BillingProviderNPI = "1234567890",
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 75.00m,
            ServiceDateFrom = new DateTime(2026, 3, 2),
            ServiceDateTo = new DateTime(2026, 3, 2),
            Status = ClaimStatus.Approved,
            SubmittedDate = new DateTime(2026, 3, 2),
            EDI835ControlNumber = "PCN-ABC-123"
        };

        var cfg = new ClaimAcknowledgmentConfig();

        var edi = service.Generate277CA(claim, cfg);

        Assert.Contains("REF*1K*PCN-ABC-123~", edi);
    }
}
