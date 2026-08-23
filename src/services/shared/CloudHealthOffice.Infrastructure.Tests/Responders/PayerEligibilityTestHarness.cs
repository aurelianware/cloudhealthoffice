using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using CloudHealthOffice.Infrastructure.Tests.Gateways;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

internal sealed class PayerEligibilityTestHarness
{
    public InMemoryPayerEligibilityDirectory Directory { get; }

    public PayerEligibilityRouter Router { get; }

    public CloudHealthOfficeEligibilityResponder Responder { get; }

    public CanonicalInboundEligibilityAdapter Adapter { get; }

    public CapturingLogger<CloudHealthOfficeEligibilityResponder> Logger { get; }

    public PayerEligibilityTestHarness()
    {
        Directory = new InMemoryPayerEligibilityDirectory();
        Router = new PayerEligibilityRouter(Directory);
        Logger = new CapturingLogger<CloudHealthOfficeEligibilityResponder>();
        Responder = new CloudHealthOfficeEligibilityResponder(Router, Directory, Logger);
        Adapter = new CanonicalInboundEligibilityAdapter(Responder);
    }

    public static PayerEligibilityInquiry SelfInquiry(
        string? payerId = ChoDemoEligibilitySeed.ExternalPayerId,
        string? memberId = ChoDemoEligibilitySeed.SubscriberMemberId,
        DateOnly? serviceDate = null,
        string serviceType = ServiceTypeCode.HealthBenefitPlanCoverage,
        string? npi = ChoDemoEligibilitySeed.InNetworkNpi,
        string? claimedTenantId = "untrusted-tenant") =>
        new()
        {
            TransactionId = "txn-self-001",
            CorrelationId = "corr-self-001",
            PayerId = payerId,
            ClaimedTenantId = claimedTenantId,
            AdapterName = CanonicalInboundEligibilityAdapter.AdapterName,
            RequestingProvider = npi is null
                ? null
                : new PayerEligibilityProvider { Npi = npi, OrganizationName = "ACME Health Services" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = memberId,
                FirstName = ChoDemoEligibilitySeed.SubscriberFirstName,
                LastName = ChoDemoEligibilitySeed.SubscriberLastName,
                DateOfBirth = ChoDemoEligibilitySeed.SubscriberDateOfBirth,
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
            },
            ServiceTypeCodes = new List<string> { serviceType },
            DateOfService = serviceDate ?? new DateOnly(2026, 8, 23)
        };

    public static PayerEligibilityInquiry DependentInquiry(
        string? payerId = ChoDemoEligibilitySeed.ExternalPayerId) =>
        new()
        {
            TransactionId = "txn-dep-001",
            CorrelationId = "corr-dep-001",
            PayerId = payerId,
            AdapterName = CanonicalInboundEligibilityAdapter.AdapterName,
            RequestingProvider = new PayerEligibilityProvider
            {
                Npi = ChoDemoEligibilitySeed.InNetworkNpi,
                OrganizationName = "ACME Health Services"
            },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
                FirstName = ChoDemoEligibilitySeed.SubscriberFirstName,
                LastName = ChoDemoEligibilitySeed.SubscriberLastName,
                DateOfBirth = ChoDemoEligibilitySeed.SubscriberDateOfBirth,
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
            },
            Patient = new GatewayEligibilityPerson
            {
                MemberId = ChoDemoEligibilitySeed.DependentMemberId,
                FirstName = ChoDemoEligibilitySeed.DependentFirstName,
                LastName = ChoDemoEligibilitySeed.DependentLastName,
                DateOfBirth = ChoDemoEligibilitySeed.DependentDateOfBirth,
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Child
            },
            ServiceTypeCodes = new List<string> { ServiceTypeCode.HealthBenefitPlanCoverage },
            DateOfService = new DateOnly(2026, 8, 23)
        };
}
