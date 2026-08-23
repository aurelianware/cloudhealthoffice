using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Covers canonical &lt;-&gt; Stedi translation (task sections 8 and 10): request
/// mapping and normalization of active/inactive coverage, cost-share, deductible,
/// out-of-pocket, dates, service types, messages, and payer rejections.
/// </summary>
public class StediEligibilityMapperTests
{
    [Fact]
    public void ToStediRequest_MapsCanonicalFields()
    {
        var request = new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "MBR123",
            GroupNumber = "GRP9",
            ProviderNpi = "1234567890",
            ServiceTypeCode = "30",
            ServiceDate = new DateOnly(2026, 6, 1),
            SubscriberFirstName = "Jane",
            SubscriberLastName = "Doe",
            SubscriberDateOfBirth = new DateOnly(1985, 4, 12),
            CorrelationId = "corr-1"
        };

        var dto = StediEligibilityMapper.ToStediRequest(request, "60054");

        dto.TradingPartnerServiceId.Should().Be("60054");
        dto.Provider.Npi.Should().Be("1234567890");
        dto.Subscriber.MemberId.Should().Be("MBR123");
        dto.Subscriber.FirstName.Should().Be("Jane");
        dto.Subscriber.LastName.Should().Be("Doe");
        dto.Subscriber.DateOfBirth.Should().Be("19850412");
        dto.Subscriber.GroupNumber.Should().Be("GRP9");
        dto.Encounter!.ServiceTypeCodes.Should().ContainSingle().Which.Should().Be("30");
        dto.Encounter.DateOfService.Should().Be("20260601");
        dto.ExternalPatientId.Should().Be("corr-1");
    }

    [Fact]
    public void ToStediRequest_DateRange_UsesBeginningAndEnd()
    {
        var request = new GatewayEligibilityRequest
        {
            TenantId = "t", SubscriberId = "M", ProviderNpi = "1",
            ServiceDate = new DateOnly(2026, 1, 1),
            ServiceDateTo = new DateOnly(2026, 1, 31)
        };

        var dto = StediEligibilityMapper.ToStediRequest(request, "60054");

        dto.Encounter!.BeginningDateOfService.Should().Be("20260101");
        dto.Encounter.EndDateOfService.Should().Be("20260131");
        dto.Encounter.DateOfService.Should().BeNull();
    }

    [Fact]
    public void ToCanonical_ActiveCoverage_WithDatesAndPlan()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1", Status = "Active Coverage" } },
            PlanInformation = new StediPlanInformationDto
            {
                GroupNumber = "GRP9", GroupDescription = "Gold PPO", PlanNumber = "PLAN-1"
            },
            PlanDateInformation = new StediPlanDateInformationDto
            {
                EligibilityBegin = "20260101", EligibilityEnd = "20261231"
            },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto { Code = "1", Name = "Health Benefit Plan Coverage", ServiceTypeCodes = new() { "30" } }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.IsEligible.Should().BeTrue();
        result.CoverageStatus.Should().Be(GatewayCoverageStatus.Active);
        result.StatusCode.Should().Be("1");
        result.PlanId.Should().Be("PLAN-1");
        result.PlanName.Should().Be("Gold PPO");
        result.GroupNumber.Should().Be("GRP9");
        result.CoverageStart.Should().Be(new DateOnly(2026, 1, 1));
        result.CoverageEnd.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void ToCanonical_InactiveCoverage()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "6", Status = "Inactive" } }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.IsEligible.Should().BeFalse();
        result.CoverageStatus.Should().Be(GatewayCoverageStatus.Inactive);
        result.StatusCode.Should().Be("6");
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToCanonical_MapsCopayCoinsuranceDeductibleAndOutOfPocket()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1" } },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto
                {
                    Code = "B", Name = "Co-Payment", ServiceTypeCodes = new() { "30" },
                    BenefitAmount = "25.00", CoverageLevelCode = "IND", InPlanNetworkIndicatorCode = "Y"
                },
                new StediBenefitInformationDto
                {
                    Code = "A", Name = "Co-Insurance", ServiceTypeCodes = new() { "30" },
                    BenefitPercent = "0.2"
                },
                new StediBenefitInformationDto
                {
                    Code = "C", Name = "Deductible", ServiceTypeCodes = new() { "30" },
                    TimeQualifierCode = "23", TimeQualifier = "Calendar Year", BenefitAmount = "1500"
                },
                new StediBenefitInformationDto
                {
                    Code = "C", Name = "Deductible", ServiceTypeCodes = new() { "30" },
                    TimeQualifier = "Remaining", BenefitAmount = "900"
                },
                new StediBenefitInformationDto
                {
                    Code = "G", Name = "Out of Pocket (Stop Loss)", ServiceTypeCodes = new() { "30" },
                    BenefitAmount = "5000", AdditionalInformation = new() { new StediAdditionalInformationDto { Description = "Family OOP" } }
                }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.Benefits.Should().HaveCount(5);

        var copay = result.Benefits.Single(b => b.BenefitCode == "B");
        copay.CopayAmount.Should().Be(25.00m);
        copay.Amount.Should().Be(25.00m);
        copay.CoverageLevel.Should().Be("IND");
        copay.InNetwork.Should().BeTrue();

        var coins = result.Benefits.Single(b => b.BenefitCode == "A");
        coins.CoinsurancePercent.Should().Be(0.2m);

        var deductibles = result.Benefits.Where(b => b.BenefitCode == "C").ToList();
        deductibles.Should().HaveCount(2);
        deductibles.Should().Contain(b => b.TimePeriod == "Calendar Year" && b.Amount == 1500m);
        deductibles.Should().Contain(b => b.TimePeriod == "Remaining" && b.Amount == 900m);

        var oop = result.Benefits.Single(b => b.BenefitCode == "G");
        oop.Amount.Should().Be(5000m);
        oop.Messages.Should().ContainSingle().Which.Should().Be("Family OOP");
    }

    [Fact]
    public void ToCanonical_NormalizesWholeNumberPercent()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1" } },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto { Code = "A", BenefitPercent = "20" }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.Benefits.Single().CoinsurancePercent.Should().Be(0.20m);
    }

    [Fact]
    public void ToCanonical_OutOfNetworkIndicator_IsRespected()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1" } },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto { Code = "B", InPlanNetworkIndicatorCode = "N", BenefitAmount = "50" }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.Benefits.Single().InNetwork.Should().BeFalse();
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData(null, true)]
    [InlineData("N", false)]
    [InlineData("W", false)]
    [InlineData("U", false)]
    public void ToCanonical_NetworkIndicator_OnlyYOrOmittedIsInNetwork(string? indicator, bool expectedInNetwork)
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1" } },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto { Code = "B", InPlanNetworkIndicatorCode = indicator, BenefitAmount = "50" }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.Benefits.Single().InNetwork.Should().Be(expectedInNetwork);
    }

    [Fact]
    public void ToCanonical_PayerRejection_IsSurfacedAsUnknownWithReason()
    {
        var stedi = new StediEligibilityResponseDto
        {
            Errors = new()
            {
                new StediErrorDto { Code = "72", Description = "Invalid/Missing Subscriber ID" }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.IsEligible.Should().BeFalse();
        result.CoverageStatus.Should().Be(GatewayCoverageStatus.Unknown);
        result.RejectionReason.Should().Contain("Invalid/Missing Subscriber ID");
    }

    [Fact]
    public void ToCanonical_AuthorizationRequired_IsMapped()
    {
        var stedi = new StediEligibilityResponseDto
        {
            PlanStatus = new() { new StediPlanStatusDto { StatusCode = "1" } },
            BenefitsInformation = new()
            {
                new StediBenefitInformationDto { Code = "1", AuthOrCertIndicator = "Y" }
            }
        };

        var result = StediEligibilityMapper.ToCanonicalResponse(stedi);

        result.Benefits.Single().AuthorizationRequired.Should().BeTrue();
    }
}
