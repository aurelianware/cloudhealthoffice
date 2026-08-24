using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

public class StediPayerDirectoryClientTests
{
    private const string Page1 = """
        {"items":[{"stediId":"AAAAA","displayName":"Synthetic Alpha","primaryPayerId":"60054","aliases":["SYN-A"],"transactionSupport":{"eligibilityCheck":"SUPPORTED","claimStatus":"NOT_SUPPORTED","claimSubmission":"NOT_SUPPORTED","claimPayment":"NOT_SUPPORTED","coordinationOfBenefits":"NOT_SUPPORTED","dentalClaimSubmission":"NOT_SUPPORTED","institutionalClaimSubmission":"NOT_SUPPORTED","professionalClaimSubmission":"NOT_SUPPORTED","unsolicitedClaimAttachment":"NOT_SUPPORTED"}}],"nextPageToken":"page-2"}
        """;

    private const string Page2 = """
        {"items":[{"stediId":"BBBBB","displayName":"Synthetic Beta","primaryPayerId":"60055","aliases":["SYN-B"],"transactionSupport":{"eligibilityCheck":"ENROLLMENT_REQUIRED","claimStatus":"NOT_SUPPORTED","claimSubmission":"NOT_SUPPORTED","claimPayment":"NOT_SUPPORTED","coordinationOfBenefits":"NOT_SUPPORTED","dentalClaimSubmission":"NOT_SUPPORTED","institutionalClaimSubmission":"NOT_SUPPORTED","professionalClaimSubmission":"NOT_SUPPORTED","unsolicitedClaimAttachment":"NOT_SUPPORTED"}}]}
        """;

    [Fact]
    public async Task ListAll_PaginatesAndAuthenticates()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, Page1)
            .EnqueueJson(HttpStatusCode.OK, Page2);
        var client = NewClient(handler);

        var payers = await client.ListAllAsync(CancellationToken.None);

        payers.Should().HaveCount(2);
        payers[0].StediId.Should().Be("AAAAA");
        payers[1].StediId.Should().Be("BBBBB");
        handler.CallCount.Should().Be(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("pageSize=100");
        handler.Requests[1].RequestUri!.Query.Should().Contain("pageToken=page-2");
        handler.Requests[0].Headers.TryGetValues("Authorization", out var auth).Should().BeTrue();
        auth!.Single().Should().Be("test-key");
    }

    [Fact]
    public async Task Unauthorized_ThrowsAuthentication_NoRetry()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var client = NewClient(handler, maxRetries: 2);

        var act = () => client.ListAllAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.Authentication);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RateLimited_ThenSuccess_Retries()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueJson(HttpStatusCode.OK, Page2);
        var client = NewClient(handler);

        var payers = await client.ListAllAsync(CancellationToken.None);

        payers.Should().ContainSingle();
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task MalformedJson_ThrowsMalformedResponse()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, "{not-json");
        var client = NewClient(handler, maxRetries: 0);

        var act = () => client.ListAllAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.MalformedResponse);
    }

    [Fact]
    public async Task LogsDoNotContainApiKey()
    {
        const string apiKey = "SUPER-SECRET-PAYER-KEY";
        var logger = new CloudHealthOffice.Infrastructure.Tests.Gateways.CapturingLogger<StediPayerDirectoryClient>();
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, Page2);
        var client = NewClient(handler, maxRetries: 1, apiKey: apiKey, logger: logger);

        await client.ListAllAsync(CancellationToken.None);

        string.Join("\n", logger.Messages).Should().NotContain(apiKey);
        logger.Messages.Should().NotBeEmpty();
    }

    private static StediPayerDirectoryClient NewClient(
        StubHttpMessageHandler handler,
        int maxRetries = 2,
        string apiKey = "test-key",
        Microsoft.Extensions.Logging.ILogger<StediPayerDirectoryClient>? logger = null)
    {
        var gateway = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://healthcare.test",
            PayerDirectoryBaseUrl = "https://payers.test",
            PayerDirectoryPath = "/2024-04-01/payers",
            Environment = "sandbox",
            MaxRetries = maxRetries
        });
        var reference = Options.Create(new PayerReferenceOptions());
        return new StediPayerDirectoryClient(
            new StubHttpClientFactory(handler, "https://payers.test"),
            gateway,
            reference,
            logger ?? NullLogger<StediPayerDirectoryClient>.Instance,
            delay: (_, _) => Task.CompletedTask);
    }
}
