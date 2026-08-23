using CloudHealthOffice.Infrastructure.Responders.Models;
using EligibilityService.Adapters;
using EligibilityService.Models;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

public class X12PayerEligibilityMapperTests
{
    [Fact]
    public void ToInquiry_MapsSubscriberDependentAndPayer()
    {
        var parsed = new Edi270ParseResult
        {
            InterchangeSenderId = "PROVIDER",
            InterchangeReceiverId = "19999",
            Inquiry = new EligibilityInquiry
            {
                Id = "inq-1",
                ControlNumber = "CTRL123",
                PayerId = "CHODEMO",
                PayerName = "CHO Demo Health Plan",
                ProviderNPI = "1999999984",
                SubscriberId = "MEMBER-10001",
                SubscriberFirstName = "John",
                SubscriberLastName = "Doe",
                SubscriberDOB = new DateTime(1980, 1, 15),
                DependentFirstName = "Jane",
                DependentLastName = "Doe",
                DependentDOB = new DateTime(2012, 5, 20),
                DependentRelationship = "19",
                ServiceTypeCode = "30",
                ServiceDateFrom = new DateTime(2026, 8, 23)
            }
        };

        var inquiry = X12PayerEligibilityMapper.ToInquiry(parsed);

        Assert.Equal("x12", inquiry.AdapterName);
        Assert.Equal("CHODEMO", inquiry.PayerId);
        Assert.Equal("19999", inquiry.TradingPartnerId);
        Assert.Equal("MEMBER-10001", inquiry.Subscriber!.MemberId);
        Assert.Equal("Jane", inquiry.Patient!.FirstName);
        Assert.Equal("child", inquiry.Patient.RelationshipToSubscriber);
        Assert.True(inquiry.IsDependentInquiry());
        Assert.Equal(new DateOnly(2026, 8, 23), inquiry.DateOfService);
        Assert.Equal("1999999984", inquiry.RequestingProvider!.Npi);
    }

    [Fact]
    public void ToAaaCode_MapsBusinessStatusNotTheOtherWayAround()
    {
        Assert.Equal("75", X12PayerEligibilityMapper.ToAaaCode(EligibilityBusinessStatus.SubscriberNotFound));
        Assert.Equal("67", X12PayerEligibilityMapper.ToAaaCode(EligibilityBusinessStatus.DependentNotFound));
        Assert.Equal("79", X12PayerEligibilityMapper.ToAaaCode(EligibilityBusinessStatus.InvalidPayer));
        Assert.Equal("57", X12PayerEligibilityMapper.ToAaaCode(EligibilityBusinessStatus.InvalidDate));
        Assert.Equal(string.Empty, X12PayerEligibilityMapper.ToAaaCode(EligibilityBusinessStatus.Success));
    }

    [Fact]
    public void ToServiceResponse_PreservesCostShare()
    {
        var canonical = new PayerEligibilityResponse
        {
            ChoTransactionId = "cho-1",
            TenantId = "cho-demo",
            TransportStatus = EligibilityTransportStatus.Success,
            BusinessStatus = EligibilityBusinessStatus.Success,
            CoverageStatus = PayerEligibilityCoverageStatus.Active,
            PlanName = "Demo PPO",
            GroupNumber = "GRP-DEMO-001",
            Deductible = new PayerEligibilityCostShare { IndividualAmount = 1500m, IndividualRemaining = 800m },
            OutOfPocket = new PayerEligibilityCostShare { IndividualAmount = 5000m, IndividualRemaining = 3200m }
        };

        var mapped = X12PayerEligibilityMapper.ToServiceResponse(new EligibilityInquiry { Id = "inq", ControlNumber = "CTRL" }, canonical);

        Assert.True(mapped.IsCovered);
        Assert.Equal("1", mapped.StatusCode);
        Assert.Equal(800m, mapped.Deductible!.IndividualDeductibleRemaining);
        Assert.Equal(3200m, mapped.OutOfPocket!.IndividualOOPRemaining);
        Assert.Equal("Demo PPO", mapped.InsurancePlanName);
    }
}
