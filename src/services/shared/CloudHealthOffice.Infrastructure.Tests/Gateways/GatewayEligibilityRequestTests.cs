using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class GatewayEligibilityRequestTests
{
    [Fact]
    public void SubscriberOnly_IsNotADependentInquiry()
    {
        var request = new GatewayEligibilityRequest
        {
            SubscriberId = "UHC202649",
            SubscriberFirstName = "John",
            SubscriberLastName = "Doe"
        };

        request.IsDependentInquiry().Should().BeFalse();
        request.ResolveSubscriberMemberId().Should().Be("UHC202649");
    }

    [Fact]
    public void SelfRelationship_IsNotADependentInquiry()
    {
        var request = new GatewayEligibilityRequest
        {
            SubscriberId = "UHC202649",
            Patient = new GatewayEligibilityPerson
            {
                MemberId = "UHC202649",
                FirstName = "John",
                LastName = "Doe",
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
            }
        };

        request.IsDependentInquiry().Should().BeFalse();
    }

    [Fact]
    public void DistinctPatient_IsDependentInquiry()
    {
        var request = new GatewayEligibilityRequest
        {
            SubscriberId = "UHC202649",
            SubscriberFirstName = "John",
            SubscriberLastName = "Doe",
            Patient = new GatewayEligibilityPerson
            {
                FirstName = "Jane",
                LastName = "Doe",
                DateOfBirth = new DateOnly(1952, 11, 21),
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Spouse
            }
        };

        request.IsDependentInquiry().Should().BeTrue();
    }

    [Fact]
    public void NestedSubscriber_ResolvesMemberIdWithoutFlatSubscriberId()
    {
        var request = new GatewayEligibilityRequest
        {
            Subscriber = new GatewayEligibilityPerson { MemberId = "NESTED-ID" }
        };

        request.ResolveSubscriberMemberId().Should().Be("NESTED-ID");
    }

    [Fact]
    public void MemberIdAlone_IsNotADependentInquiry()
    {
        var request = new GatewayEligibilityRequest
        {
            SubscriberId = "UHC202649",
            MemberId = "DEP-1"
        };

        request.IsDependentInquiry().Should().BeFalse();
    }
}
