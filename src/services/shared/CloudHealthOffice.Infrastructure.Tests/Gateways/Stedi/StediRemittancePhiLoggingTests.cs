using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediRemittancePhiLoggingTests
{
    [Fact]
    public async Task RetrieveAndProcess_DoesNotLogPhiBankOrRawEra()
    {
        const string apiKey = "SUPER-SECRET-KEY";
        const string memberId = "SECRETMEMBER123";
        const string lastName = "Zzyphisurname";
        const string trace = "EFT-TRACE-SECRET";

        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            RemittanceReportPath = "/835/{transactionId}",
            MaxRetries = 1
        });

        var json = StediRemittanceTests.PaidJson
            .Replace("CLM-P-1001", memberId)
            .Replace("EFT-TRACE-1", trace);
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, json);

        var apiLogger = new CapturingLogger<StediRemittanceApiClient>();
        var gatewayLogger = new CapturingLogger<StediHealthcareGateway>();
        var processorLogger = new CapturingLogger<RemittanceProcessor>();
        var factory = new StubHttpClientFactory(handler);
        var gateway = new StediHealthcareGateway(
            new StediEligibilityApiClient(factory, options, NullLogger<StediEligibilityApiClient>.Instance,
                delay: (_, _) => Task.CompletedTask),
            PayerTestHarness.CreateResolver(options),
            options,
            gatewayLogger,
            remittanceClient: new StediRemittanceApiClient(
                factory, options, apiLogger, delay: (_, _) => Task.CompletedTask));

        var retrieved = await gateway.RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        retrieved.IsSuccess.Should().BeTrue();

        var transmissions = new InMemoryClaimTransmissionStore();
        await transmissions.SaveAsync(new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            PatientControlNumber = memberId,
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        });
        var processor = new RemittanceProcessor(
            new InMemoryRemittanceStore(), transmissions, processorLogger);
        await processor.ProcessAsync(retrieved.Result!);

        var allLogs = string.Join("\n",
            apiLogger.Messages.Concat(gatewayLogger.Messages).Concat(processorLogger.Messages));
        allLogs.Should().NotBeEmpty();
        allLogs.Should().NotContain(apiKey);
        allLogs.Should().NotContain(memberId);
        allLogs.Should().NotContain(lastName);
        allLogs.Should().NotContain(trace);
        allLogs.Should().NotContain("totalClaimChargeAmount");
        allLogs.Should().NotContain("ISA*");
        allLogs.Should().Contain("Remittance835");
    }
}
