using System.Net;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Covers task section 14 and the security acceptance criteria: the API key,
/// subscriber/member identifiers, names, dates of birth, and raw request/response
/// bodies never reach the logger. Both the gateway and the transport client are
/// exercised (including a retry, which is the only path the client logs on).
/// </summary>
public class StediPhiLoggingTests
{
    private const string ActiveJson =
        "{\"meta\":{\"traceId\":\"trace-xyz\"},\"planStatus\":[{\"statusCode\":\"1\"}]," +
        "\"benefitsInformation\":[{\"code\":\"1\",\"name\":\"Health Benefit Plan Coverage\"}]}";

    [Fact]
    public async Task Eligibility_DoesNotLogApiKeyPhiOrBodies()
    {
        const string apiKey = "SUPER-SECRET-KEY";
        const string memberId = "SECRETMEMBER123";
        const string lastName = "Zzyphisurname";

        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            MaxRetries = 1
        });

        // 500 then 200 forces the transport client to log a retry warning too.
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);

        var apiLogger = new CapturingLogger<StediEligibilityApiClient>();
        var gatewayLogger = new CapturingLogger<StediHealthcareGateway>();

        var apiClient = new StediEligibilityApiClient(
            new StubHttpClientFactory(handler), options, apiLogger, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            apiClient, PayerTestHarness.CreateResolver(options), options, gatewayLogger);

        await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = memberId,
            SubscriberLastName = lastName,
            SubscriberDateOfBirth = new DateOnly(1980, 7, 4),
            ProviderNpi = "1234567890",
            ServiceTypeCode = "30",
            PayerId = "60054",
            CorrelationId = "corr-1"
        });

        var logs = string.Join("\n", apiLogger.Messages.Concat(gatewayLogger.Messages));

        logs.Should().NotBeEmpty();
        logs.Should().NotContain(apiKey);
        logs.Should().NotContain(memberId);
        logs.Should().NotContain(lastName);
        logs.Should().NotMatchRegex(@"\b1980\b");
        // Raw response body markers must never be logged.
        logs.Should().NotContain("planStatus");
        logs.Should().NotContain("benefitsInformation");
        // Non-PHI operational fields are expected.
        logs.Should().Contain("tenant-alpha");
        logs.Should().Contain("Stedi");
    }

    [Fact]
    public async Task Logging_StripsNewlinesFromUserInfluencedValues()
    {
        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = "k", BaseUrl = "https://healthcare.test", Environment = "sandbox",
            EligibilityPath = "/eligibility/v3", MaxRetries = 0
        });
        var handler = new StubHttpMessageHandler().EnqueueJson(
            System.Net.HttpStatusCode.OK, "{\"planStatus\":[{\"statusCode\":\"1\"}]}");
        var logger = new CapturingLogger<StediHealthcareGateway>();
        var apiClient = new StediEligibilityApiClient(
            new StubHttpClientFactory(handler), options,
            new CapturingLogger<StediEligibilityApiClient>(), delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            apiClient, PayerTestHarness.CreateResolver(options), options, logger);

        await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha\r\nINJECTED forged log line",
            SubscriberId = "M1",
            ProviderNpi = "1",
            PayerId = "60054",
            CorrelationId = "corr\r\nALSO-INJECTED"
        });

        foreach (var message in logger.Messages)
        {
            message.Should().NotContain("\n");
            message.Should().NotContain("\r");
        }
    }
}
