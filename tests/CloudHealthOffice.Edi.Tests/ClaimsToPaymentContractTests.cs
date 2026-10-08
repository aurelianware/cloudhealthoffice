using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Claims = ClaimsService.Models;
using Pay = PaymentService.Services;
using PayModels = PaymentService.Models;

namespace CloudHealthOffice.Edi.Tests;

/// <summary>
/// Contract between claims-service's claim search (it returns
/// <see cref="Claims.Claim"/> through plain <c>AddControllers()</c>: camelCase
/// properties, enums as numbers) and payment-service's <see cref="Pay.ClaimDto"/>.
/// This seam hid the billed-charge bug: payment-service read fields
/// (approvedAmount, claimLines[].paidAmount, patientResponsibility) that
/// claims-service never sends. These tests serialize the real claims-service
/// model and read it the way payment-service does.
/// </summary>
public class ClaimsToPaymentContractTests
{
    // ASP.NET Core MVC's default output options (AddControllers without AddJsonOptions).
    private static readonly JsonSerializerOptions ClaimsServiceWire = new(JsonSerializerDefaults.Web);

    private static Claims.Claim AdjudicatedClaim(Claims.ClaimStatus status) => new()
    {
        Id = "clm-1",
        ClaimNumber = "CLM-0001",
        MemberId = "M-1",
        BillingProviderNPI = "1234567893",
        TotalChargeAmount = 300m,
        Status = status,
        ServiceDateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new Claims.AdjudicationResult
        {
            AllowedAmount = 220m,      // includes member cost share: never the amount paid
            DeductibleAmount = 40m,
            CoinsuranceAmount = 10m,
            CopayAmount = 0m,
            PatientResponsibility = 50m,
            PayerPayment = 170m,       // what the plan pays: CLP04
            AdjustmentReasons = { new Claims.ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = 80m } },
        },
        ClaimLines =
        {
            new Claims.ClaimLine
            {
                LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1,
                AdjudicationResult = new Claims.LineAdjudicationResult { AllowedAmount = 150m, PaidAmount = 120m, PatientResponsibility = 30m },
            },
            new Claims.ClaimLine
            {
                LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 100m, Units = 1,
                AdjudicationResult = new Claims.LineAdjudicationResult { AllowedAmount = 70m, PaidAmount = 50m, PatientResponsibility = 20m },
            },
        },
    };

    /// <summary>Serialize as claims-service does, read as payment-service does (ReadFromJsonAsync).</summary>
    private static async Task<Pay.ClaimDto> RoundTrip(Claims.Claim claim)
    {
        var json = JsonSerializer.Serialize(new[] { claim }, ClaimsServiceWire);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var dtos = await content.ReadFromJsonAsync<List<Pay.ClaimDto>>();
        return Assert.Single(dtos!);
    }

    [Fact]
    public async Task ApprovedClaim_PlanPaidAmount_IsAdjudicationResultPayerPayment_NotAllowedOrBilled()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));

        Assert.Equal(170m, dto.PlanPaidAmount);
        Assert.NotEqual(dto.AdjudicationResult!.AllowedAmount, dto.PlanPaidAmount);
        Assert.Equal(300m, dto.TotalChargeAmount);                 // CLP03
        Assert.Equal(50m, dto.AdjudicationResult.PatientResponsibility); // CLP05
    }

    [Fact]
    public async Task ServiceLinePaidAmounts_ComeFromLineAdjudicationResult_AndSumToPayerPayment()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));

        Assert.Equal(new decimal?[] { 120m, 50m }, dto.ServiceLines!.Select(l => l.LinePaidAmount));
        Assert.Equal(dto.PlanPaidAmount, dto.ServiceLines!.Sum(l => l.LinePaidAmount));
    }

    [Theory]
    [InlineData(Claims.ClaimStatus.Approved, PayModels.ClaimStatus.Approved)]
    [InlineData(Claims.ClaimStatus.Pended, PayModels.ClaimStatus.Pended)]
    [InlineData(Claims.ClaimStatus.Denied, PayModels.ClaimStatus.Denied)]
    [InlineData(Claims.ClaimStatus.Paid, PayModels.ClaimStatus.Paid)]
    [InlineData(Claims.ClaimStatus.Voided, PayModels.ClaimStatus.Voided)]
    public async Task ClaimStatus_NumericWireValue_MapsToTheSameStatus(Claims.ClaimStatus sent, PayModels.ClaimStatus read)
    {
        var dto = await RoundTrip(AdjudicatedClaim(sent));

        Assert.Equal(read, dto.Status);
    }

    [Fact]
    public void EveryClaimsServiceStatus_HasTheSameNameAndValueInPaymentService()
    {
        foreach (var status in Enum.GetValues<Claims.ClaimStatus>())
        {
            Assert.True(Enum.TryParse<PayModels.ClaimStatus>(status.ToString(), out var mirrored), $"{status} missing");
            Assert.Equal((int)status, (int)mirrored);
        }
    }

    [Fact]
    public async Task ClaimWithoutAdjudicationResult_HasNoPlanPaidAmount()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.AdjudicationResult = null;
        foreach (var line in claim.ClaimLines)
            line.AdjudicationResult = null;

        var dto = await RoundTrip(claim);

        Assert.Null(dto.PlanPaidAmount);
        Assert.All(dto.ServiceLines!, l => Assert.Null(l.LinePaidAmount));
    }
}
