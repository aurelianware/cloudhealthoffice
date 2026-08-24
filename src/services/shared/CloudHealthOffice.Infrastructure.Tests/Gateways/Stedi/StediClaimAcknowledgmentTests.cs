using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimAcknowledgmentTests
{
    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        CoreBaseUrl = "https://core.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        ClaimAcknowledgmentReportPath = "/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277",
        PollTransactionsPath = "/2023-08-01/polling/transactions",
        MaxRetries = 1,
        WebhookCredentialValue = "webhook-secret"
    };

    private static StediHealthcareGateway NewGateway(
        StubHttpMessageHandler handler,
        IClaimTransmissionStore? store = null,
        StediGatewayOptions? options = null)
    {
        options ??= ValidOptions();
        var opts = Options.Create(options);
        var factory = new StubHttpClientFactory(handler);
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var claims = new StediClaimApiClient(
            factory, opts, NullLogger<StediClaimApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var acks = new StediClaimAcknowledgmentApiClient(
            factory, opts, NullLogger<StediClaimAcknowledgmentApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        return new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance, timeProvider: null, claims, store, acks);
    }

    [Fact]
    public async Task Retrieve_Accepted277CA_IsNormalized()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, StediClaimAcknowledgmentMapperTests.AcceptedJson);
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "71716ec5-0e96-462f-bb77-869941bb27ab",
            EventId = "evt-1"
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        response.Result.OriginalSubmissionId.Should().Be("synthetic-sub-001");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.ClaimAcknowledgment277CA);
        handler.Requests[0].RequestUri!.ToString().Should().Contain("/277");
        handler.Requests[0].RequestUri!.ToString().Should().Contain("71716ec5-0e96-462f-bb77-869941bb27ab");
    }

    [Fact]
    public async Task Retrieve_Http429_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueJson(HttpStatusCode.OK, StediClaimAcknowledgmentMapperTests.AcceptedJson);
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "71716ec5-0e96-462f-bb77-869941bb27ab"
        });

        response.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        response.Metadata.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Retrieve_Http500_IsTransientFailure()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError);
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "71716ec5-0e96-462f-bb77-869941bb27ab"
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceUnavailable);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Retrieve_Http400_IsNotRetried()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest);
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "71716ec5-0e96-462f-bb77-869941bb27ab"
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Retrieve_Timeout_IsClassified()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new TaskCanceledException())
            .EnqueueThrow(new TaskCanceledException());
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveAcknowledgmentAsync(new ClaimAcknowledgmentRetrievalRequest
        {
            ExternalAcknowledgmentId = "71716ec5-0e96-462f-bb77-869941bb27ab"
        });

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Timeout);
    }

    [Fact]
    public void ParseWebhook_ReadsTransactionProcessedPointer()
    {
        const string json = """
            {
              "version": "0",
              "id": "8a9fc08a-24b2-4eeb-af7c-f96376ea471e",
              "detail-type": "transaction.processed.v2",
              "source": "stedi.core",
              "detail": {
                "transactionId": "71716ec5-0e96-462f-bb77-869941bb27ab",
                "fileExecutionId": "95236a56-a020-4522-8fef-bcffcec0ec1d",
                "direction": "INBOUND",
                "x12": {
                  "metadata": {
                    "transaction": { "transactionSetIdentifier": "277" }
                  }
                }
              }
            }
            """;

        StediHealthcareGateway.TryParseClaimResponseEvent(json, out var discovery).Should().BeTrue();
        discovery.EventId.Should().Be("8a9fc08a-24b2-4eeb-af7c-f96376ea471e");
        discovery.ExternalAcknowledgmentId.Should().Be("71716ec5-0e96-462f-bb77-869941bb27ab");
        discovery.Direction.Should().Be("INBOUND");
        discovery.TransactionSetIdentifier.Should().Be("277");
        discovery.GatewayName.Should().Be("Stedi");
    }

    [Fact]
    public void Ingress_IgnoresOutboundAnd835()
    {
        ClaimAcknowledgmentIngress.IsInbound277(new ClaimAcknowledgmentDiscovery
        {
            TransactionSetIdentifier = "835",
            Direction = "INBOUND"
        }, out var reason).Should().BeFalse();
        reason.Should().Be("unsupported-transaction-set");

        ClaimAcknowledgmentIngress.IsInbound277(new ClaimAcknowledgmentDiscovery
        {
            TransactionSetIdentifier = "277",
            Direction = "OUTBOUND"
        }, out _).Should().BeFalse();

        ClaimAcknowledgmentIngress.IsInbound277(new ClaimAcknowledgmentDiscovery
        {
            ExternalAcknowledgmentId = "only-id"
        }, out var missing).Should().BeFalse();
        missing.Should().Be("unsupported-transaction-set");
    }

    [Fact]
    public void ParseWebhook_NonObjectJson_IsFalse()
    {
        StediHealthcareGateway.TryParseClaimResponseEvent("null", out _).Should().BeFalse();
        StediHealthcareGateway.TryParseClaimResponseEvent("[]", out _).Should().BeFalse();
    }

    [Fact]
    public void ParseWebhook_NonProcessedEvent_IsIgnored()
    {
        const string json = """
            {
              "id": "evt-failed",
              "detail-type": "file.failed.v2",
              "detail": {
                "transactionId": "71716ec5-0e96-462f-bb77-869941bb27ab",
                "direction": "INBOUND",
                "x12": { "metadata": { "transaction": { "transactionSetIdentifier": "277" } } }
              }
            }
            """;

        StediHealthcareGateway.TryParseClaimResponseEvent(json, out var discovery).Should().BeTrue();
        discovery.TransactionSetIdentifier.Should().Be("ignored");
        ClaimAcknowledgmentIngress.IsInbound277(discovery, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Poll_ProcessesInbound277_AndSkipsOthers()
    {
        const string pollJson = """
            {
              "nextPageToken": "page-2",
              "items": [
                {
                  "transactionId": "71716ec5-0e96-462f-bb77-869941bb27ab",
                  "status": "succeeded",
                  "direction": "INBOUND",
                  "x12": { "metadata": { "transaction": { "transactionSetIdentifier": "277" } } }
                },
                {
                  "transactionId": "835-id",
                  "status": "succeeded",
                  "direction": "INBOUND",
                  "x12": { "metadata": { "transaction": { "transactionSetIdentifier": "835" } } }
                }
              ]
            }
            """;

        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, pollJson)
            .EnqueueJson(HttpStatusCode.OK, StediClaimAcknowledgmentMapperTests.AcceptedJson);

        var opts = Options.Create(ValidOptions());
        var factory = new StubHttpClientFactory(handler);
        var client = new StediClaimAcknowledgmentApiClient(
            factory, opts, NullLogger<StediClaimAcknowledgmentApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var store = new InMemoryClaimTransmissionStore();
        await store.SaveAsync(new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = "k",
            SubmissionId = "synthetic-sub-001",
            PatientControlNumber = "CLM-P-1001",
            SubmittedAtUtc = DateTimeOffset.UtcNow
        });
        var ackStore = new InMemoryClaimAcknowledgmentStore();
        var processor = new ClaimAcknowledgmentProcessor(
            ackStore, store, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        var resolver = BuildStediResolver(handler, store);
        var ingress = new ClaimAcknowledgmentIngress(
            resolver, processor, NullLogger<ClaimAcknowledgmentIngress>.Instance);
        var cursors = new InMemoryClaimAcknowledgmentCursorStore();
        var remittanceIngress = new RemittanceIngress(
            resolver,
            new RemittanceProcessor(
                new InMemoryRemittanceStore(), store, NullLogger<RemittanceProcessor>.Instance),
            NullLogger<RemittanceIngress>.Instance);
        var poller = new StediClaimAcknowledgmentPoller(
            client, ingress, remittanceIngress, cursors, opts,
            NullLogger<StediClaimAcknowledgmentPoller>.Instance);

        await poller.RunOnce(CancellationToken.None);

        var cursor = await cursors.GetAsync("Stedi");
        cursor!.PageToken.Should().Be("page-2");
        (await store.GetByIdempotencyKeyAsync("tenant-alpha", "k"))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Fact]
    public void WebhookCredential_FailClosedWhenUnset()
    {
        var opts = new StediGatewayOptions { WebhookCredentialValue = null };
        opts.WebhookCredentialIsValid("anything").Should().BeFalse();
    }

    [Fact]
    public void WebhookCredential_RejectsMismatch()
    {
        var opts = ValidOptions();
        opts.WebhookCredentialIsValid("wrong").Should().BeFalse();
        opts.WebhookCredentialIsValid("webhook-secret").Should().BeTrue();
    }

    private static IHealthcareGatewayResolver BuildStediResolver(
        StubHttpMessageHandler handler, IClaimTransmissionStore store)
    {
        var gateway = NewGateway(handler, store);
        return new HealthcareGatewayResolver(
            new IHealthcareTransactionGateway[] { gateway },
            Options.Create(new HealthcareTransactionOptions { DefaultGateway = "Stedi" }),
            NullLogger<HealthcareGatewayResolver>.Instance);
    }
}
