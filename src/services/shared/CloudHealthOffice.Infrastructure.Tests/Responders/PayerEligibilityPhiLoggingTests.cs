using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityPhiLoggingTests
{
    [Fact]
    public async Task Respond_DoesNotLogPhiOrRawPayloads()
    {
        var harness = new PayerEligibilityTestHarness();
        const string phiMemberId = "PHI-MEMBER-ZX9Q";
        const string phiLastName = "Zzytestphisurname";
        const string phiFirstName = "PhiFirstnameQzx";
        var phiDob = new DateOnly(1977, 3, 14);

        var inquiry = new PayerEligibilityInquiry
        {
            TransactionId = "txn-phi-001",
            CorrelationId = "corr-phi-safe",
            PayerId = ChoDemoEligibilitySeed.ExternalPayerId,
            AdapterName = "canonical",
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = phiMemberId,
                FirstName = phiFirstName,
                LastName = phiLastName,
                DateOfBirth = phiDob
            },
            RequestingProvider = new PayerEligibilityProvider { Npi = ChoDemoEligibilitySeed.InNetworkNpi },
            DateOfService = new DateOnly(2026, 8, 23),
            ServiceTypeCodes = new List<string> { ServiceTypeCode.HealthBenefitPlanCoverage }
        };

        await harness.Responder.RespondAsync(inquiry);

        harness.Logger.Messages.Should().NotBeEmpty("the responder logs non-PHI transaction metadata");
        var allLogs = string.Join("\n", harness.Logger.Messages);

        allLogs.Should().NotContain(phiMemberId);
        allLogs.Should().NotContain(phiLastName);
        allLogs.Should().NotContain(phiFirstName);
        allLogs.Should().NotMatchRegex(@"\b1977\b");
        allLogs.Should().NotContain("PayerEligibilityInquiry");
        allLogs.Should().NotContain("raw request");
        allLogs.Should().NotContain("raw response");
        allLogs.Should().NotContain(ChoDemoEligibilitySeed.SubscriberMemberId);

        allLogs.Should().Contain("Eligibility270271");
        allLogs.Should().Contain("corr-phi-safe");
        allLogs.Should().Contain("canonical");
        allLogs.Should().Contain(ChoDemoEligibilitySeed.TenantId);
    }

    [Fact]
    public async Task SuccessfulMatch_DoesNotLogSubscriberIdentity()
    {
        var harness = new PayerEligibilityTestHarness();
        await harness.Responder.RespondAsync(PayerEligibilityTestHarness.SelfInquiry());

        var allLogs = string.Join("\n", harness.Logger.Messages);
        allLogs.Should().NotContain(ChoDemoEligibilitySeed.SubscriberMemberId);
        allLogs.Should().NotContain(ChoDemoEligibilitySeed.SubscriberLastName);
        allLogs.Should().NotContain(ChoDemoEligibilitySeed.DependentMemberId);
        allLogs.Should().NotContain("John");
        allLogs.Should().NotMatchRegex(@"\b1980\b");
        allLogs.Should().Contain("Active");
        allLogs.Should().Contain("Success");
    }
}
