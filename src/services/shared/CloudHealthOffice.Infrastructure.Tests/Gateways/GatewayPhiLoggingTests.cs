using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// Covers requirement 6: PHI-bearing request/response payloads are not written
/// into normal application logs. Only non-PHI transaction metadata is logged.
/// </summary>
public class GatewayPhiLoggingTests
{
    [Fact]
    public async Task CheckEligibility_DoesNotLogPhi()
    {
        var logger = new CapturingLogger<MockHealthcareGateway>();
        var gateway = new MockHealthcareGateway(logger);

        // Distinctive PHI-bearing values that must never appear in logs.
        const string phiSubscriberId = "SUB-1001";
        const string phiLastName = "Zzytestphisurname";
        var phiDob = new DateOnly(1980, 7, 4);

        await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = phiSubscriberId,
            SubscriberLastName = phiLastName,
            SubscriberDateOfBirth = phiDob,
            ProviderNpi = "1234567890"
        });

        logger.Messages.Should().NotBeEmpty("the gateway logs non-PHI transaction metadata");

        var allLogs = string.Join("\n", logger.Messages);
        allLogs.Should().NotContain(phiSubscriberId);
        allLogs.Should().NotContain(phiLastName);
        // Word-boundary match so a random GUID (ExternalTransactionId) that
        // happens to contain the digits 1980 does not trip the assertion —
        // only the year appearing as its own token (e.g. a rendered date) fails.
        allLogs.Should().NotMatchRegex(@"\b1980\b");
        // Non-PHI metadata is expected to be present.
        allLogs.Should().Contain("tenant-alpha");
        allLogs.Should().Contain("Eligibility270271");
    }
}
