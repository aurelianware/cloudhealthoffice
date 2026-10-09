using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// 005010X221A1 CLP02 for a processed claim follows the payer sequence
/// coordination of benefits was actually applied in (claims-service
/// adjudicationResult.cobPayerSequence), not the 837 SBR01 alone: 1
/// processed as primary (no COB applied), 2 as secondary, 3 as tertiary
/// (also payers four to eleven, which the 835 has no code for).
/// </summary>
public class Era835CobClaimStatusTests
{
    [Theory]
    [InlineData(null, "1")]
    [InlineData(1, "1")]
    [InlineData(2, "2")]
    [InlineData(3, "3")]
    [InlineData(4, "3")]
    [InlineData(11, "3")]
    public void ProcessedAsCode_FromAppliedCobSequence(int? sequence, string expected) =>
        Assert.Equal(expected, Era835ClaimPaymentBuilder.ProcessedAsCode(sequence));

    [Fact]
    public void Build_UsesTheAppliedSequence_NotSbr01()
    {
        var mapper = new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance);
        var claim = new ClaimDto
        {
            Id = "c1",
            ClaimNumber = "C1",
            MemberId = "M1",
            BillingProviderNPI = "1234567893",
            TotalChargeAmount = 100m,
            // The 837 said secondary, but COB was not applied.
            PayerResponsibilityCode = "S",
            AdjudicationResult = new ClaimAdjudicationDto { AllowedAmount = 100m, PayerPayment = 80m },
        };

        Assert.Equal("1", Era835ClaimPaymentBuilder.Build(claim, denied: false, mapper).ClaimStatusCode);

        claim.AdjudicationResult.CobPayerSequence = 3;
        Assert.Equal("3", Era835ClaimPaymentBuilder.Build(claim, denied: false, mapper).ClaimStatusCode);
    }
}
