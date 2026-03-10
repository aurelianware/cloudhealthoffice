using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

public class Edi271GeneratorTests
{
    [Fact]
    public void Generate_CoveredResponse_IncludesExpectedSegments_AndSeCountMatches()
    {
        var generator = new Edi271Generator(NullLogger<Edi271Generator>.Instance);

        var inquiry = new EligibilityInquiry
        {
            Id = "inq-001",
            PayerId = "PAY01",
            PayerName = "Test Payer",
            ProviderId = "PROV01",
            ProviderNPI = "1234567890",
            SubscriberId = "SUB123",
            SubscriberFirstName = "Jane",
            SubscriberLastName = "Doe",
            SubscriberDOB = new DateTime(1980, 1, 15),
            SubscriberGender = "F"
        };

        var response = new EligibilityResponse
        {
            ControlNumber = "CTRL271",
            IsCovered = true,
            CoverageLevel = "IND",
            InsurancePlanName = "Gold Plan",
            GroupNumber = "GRP100",
            CoverageBeginDate = new DateTime(2026, 1, 1),
            CoverageEndDate = new DateTime(2026, 12, 31),
            Deductible = new DeductibleInfo { IndividualDeductible = 1000m },
            OutOfPocket = new OutOfPocketInfo { IndividualOOPMax = 3000m },
            Benefits =
            [
                new EligibilityBenefit
                {
                    ServiceTypeCode = "30",
                    ServiceTypeName = "Health Benefit Plan Coverage",
                    CoverageLevel = "IND",
                    InsuranceType = "B",
                    TimePeriodQualifier = "23",
                    MonetaryAmount = 25m,
                    NetworkIndicator = "Y",
                    AuthorizationRequired = "Y"
                }
            ]
        };

        var edi = generator.Generate(inquiry, response, isaSenderId: "PAYERISA", isaReceiverId: "PROVIDERISA");

        Assert.Contains("ST*271*0001*005010X279A1~", edi);
        Assert.Contains("GS*HB*PAY01*PROV01*", edi);
        Assert.Contains("NM1*PR*2*Test Payer*****PI*PAY01~", edi);
        Assert.Contains("NM1*1P*2*1234567890*****XX*1234567890~", edi);
        Assert.Contains("NM1*IL*1*Doe*Jane****MI*SUB123~", edi);
        Assert.Contains("REF*0F*SUB123~", edi);
        Assert.Contains("TRN*2*CTRL271*PAY01~", edi);
        Assert.Contains("DTP*346*D8*20260101~", edi);
        Assert.Contains("DTP*347*D8*20261231~", edi);
        Assert.Contains("EB*1*IND*30**Gold_Plan~", edi);
        Assert.Contains("REF*1L*GRP100~", edi);
        Assert.Contains("EB*C*IND*30***23**1000.00~", edi);
        Assert.Contains("EB*G*IND*30***23**3000.00~", edi);
        Assert.Contains("EB*B*IND*30***23**25.00****Y~", edi);
        Assert.Contains("MSG*Prior authorization required for Health Benefit Plan Coverage~", edi);

        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var stIndex = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var seIndex = Array.FindIndex(segments, s => s.StartsWith("SE*"));

        Assert.True(stIndex >= 0 && seIndex > stIndex, "ST/SE segments were not found in expected order.");

        var seParts = segments[seIndex].Split('*');
        var declaredSeCount = int.Parse(seParts[1]);
        var actualSeCount = seIndex - stIndex + 1;

        Assert.Equal(actualSeCount, declaredSeCount);
    }

    [Fact]
    public void Generate_NotCoveredResponse_IncludesAaaAndMsg()
    {
        var generator = new Edi271Generator(NullLogger<Edi271Generator>.Instance);

        var inquiry = new EligibilityInquiry
        {
            Id = "inq-002",
            PayerId = "PAY02",
            ProviderId = "PROV02",
            ProviderNPI = "1098765432",
            SubscriberId = "SUB999",
            SubscriberFirstName = "John",
            SubscriberLastName = "Smith"
        };

        var response = new EligibilityResponse
        {
            ControlNumber = "CTRL272",
            IsCovered = false,
            RejectionReason = "Coverage terminated"
        };

        var edi = generator.Generate(inquiry, response, isaSenderId: "", isaReceiverId: "");

        Assert.Contains("EB*6**30~", edi);
        Assert.Contains("AAA*N**42*Y~", edi);
        Assert.Contains("MSG*Coverage terminated~", edi);
    }
}
