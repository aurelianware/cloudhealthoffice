using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimStatusPhiLoggingTests
{
    [Fact]
    public async Task ClaimStatus_DoesNotLogMemberIdNameDobRawJsonOrApiKey()
    {
        const string apiKey = "SUPER-SECRET-KEY";
        const string memberId = "SECRETMEMBER123";
        const string lastName = "Zzyphisurname";
        const string firstName = "PhiFirst";
        var dob = new DateOnly(1980, 7, 4);

        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            ClaimStatusPath = "/2024-04-01/change/medicalnetwork/claimstatus/v2",
            MaxRetries = 1
        });

        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK,
                """{"meta":{"traceId":"trace-276"},"claims":[{"claimStatus":{"statusCategoryCode":"P1","statusCode":"20","patientAccountNumber":"SECRETMEMBER123"}}]}""");

        var apiLogger = new CapturingLogger<StediClaimStatusApiClient>();
        var gatewayLogger = new CapturingLogger<StediHealthcareGateway>();
        var transmissions = new InMemoryClaimTransmissionStore();
        var factory = new StubHttpClientFactory(handler);
        var statusClient = new StediClaimStatusApiClient(
            factory, options, apiLogger, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            new StediEligibilityApiClient(factory, options, NullLogger<StediEligibilityApiClient>.Instance,
                delay: (_, _) => Task.CompletedTask),
            PayerTestHarness.CreateResolver(options),
            options,
            gatewayLogger,
            transmissions: transmissions,
            statusInquiries: new InMemoryClaimStatusInquiryStore(),
            statusClient: statusClient);

        var source = GatewayClaimFixtures.Professional();
        source.Subscriber = new GatewayEligibilityPerson
        {
            MemberId = memberId,
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = dob
        };
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            PayerId = "60054",
            PatientControlNumber = "CLM-P-1001",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            InquirySource = ClaimStatusInquirySource.FromSubmission(source),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            CorrelationId = "corr-phi"
        });

        var allLogs = string.Join("\n", apiLogger.Messages.Concat(gatewayLogger.Messages));
        allLogs.Should().NotBeEmpty();
        allLogs.Should().NotContain(apiKey);
        allLogs.Should().NotContain(memberId);
        allLogs.Should().NotContain(lastName);
        allLogs.Should().NotContain(firstName);
        allLogs.Should().NotMatchRegex(@"\b1980\b");
        allLogs.Should().NotContain("tradingPartnerServiceId");
        allLogs.Should().NotContain("statusCategoryCode");
        allLogs.Should().NotContain("ISA*");
        allLogs.Should().Contain("ClaimStatus276277");
        allLogs.Should().Contain("tenant-alpha");
    }
}

public class ClaimStatusPhiLoggingTests
{
    [Fact]
    public async Task MockClaimStatus_DoesNotLogMemberIdNameOrDob()
    {
        var logger = new CapturingLogger<MockHealthcareGateway>();
        var transmissions = new InMemoryClaimTransmissionStore();
        var gateway = new MockHealthcareGateway(logger, transmissions: transmissions);
        var request = GatewayClaimFixtures.Professional();
        request.Subscriber = new GatewayEligibilityPerson
        {
            MemberId = "SECRETMEMBER123",
            FirstName = "PhiFirst",
            LastName = "Zzyphisurname",
            DateOfBirth = new DateOnly(1980, 7, 4)
        };
        await gateway.SubmitClaimAsync(request);
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        var allLogs = string.Join("\n", logger.Messages);
        allLogs.Should().NotContain("SECRETMEMBER123");
        allLogs.Should().NotContain("Zzyphisurname");
        allLogs.Should().NotContain("PhiFirst");
        allLogs.Should().NotMatchRegex(@"\b1980\b");
        allLogs.Should().Contain("ClaimStatus276277");
    }
}
