using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimAcknowledgmentPhiLoggingTests
{
    [Fact]
    public async Task RetrieveAndProcess_DoNotLogPhiSecretsOrRawPayload()
    {
        const string apiKey = "SUPER-SECRET-KEY";
        const string webhookSecret = "WEBHOOK-SECRET-VALUE";
        const string memberId = "SECRETMEMBER123";
        const string lastName = "Zzyphisurname";

        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            WebhookCredentialValue = webhookSecret,
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            ClaimAcknowledgmentReportPath = "/reports/{transactionId}/277",
            MaxRetries = 1
        });

        var rejected = StediClaimAcknowledgmentMapperTests.RejectedJson
            .Replace("ANON", lastName, StringComparison.Ordinal);

        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, rejected);

        var apiLogger = new CapturingLogger<StediClaimAcknowledgmentApiClient>();
        var gatewayLogger = new CapturingLogger<StediHealthcareGateway>();
        var processorLogger = new CapturingLogger<ClaimAcknowledgmentProcessor>();

        var factory = new StubHttpClientFactory(handler);
        var ackClient = new StediClaimAcknowledgmentApiClient(
            factory, options, apiLogger, delay: (_, _) => Task.CompletedTask);
        var eligibility = new StediEligibilityApiClient(
            factory, options, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(options), options, gatewayLogger,
            timeProvider: null, claimClient: null, transmissions: null, acknowledgmentClient: ackClient);

        var retrieved = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        });

        var store = new InMemoryClaimTransmissionStore();
        await store.SaveAsync(new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1002",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = "k",
            SubmissionId = "synthetic-sub-002",
            PatientControlNumber = "CLM-P-1002",
            SubmittedAtUtc = DateTimeOffset.UtcNow
        });
        var processor = new ClaimAcknowledgmentProcessor(
            new InMemoryClaimAcknowledgmentStore(), store, processorLogger);
        await processor.ProcessAsync(retrieved.Result!);

        var logs = string.Join("\n", apiLogger.Messages.Concat(gatewayLogger.Messages).Concat(processorLogger.Messages));
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(apiKey);
        logs.Should().NotContain(webhookSecret);
        logs.Should().NotContain(memberId);
        logs.Should().NotContain(lastName);
        logs.Should().NotContain("JOHN");
        logs.Should().NotContain("patientClaimStatusDetails");
        logs.Should().NotContain("informationClaimStatuses");
        logs.Should().Contain("tenant-alpha");
        logs.Should().Contain("Stedi");
    }
}
