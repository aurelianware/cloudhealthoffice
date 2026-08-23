using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Covers HTTP transport behaviour (task section 6/17): success, auth/authz
/// failures, invalid request, rate limiting, timeout, transient network errors,
/// 5xx, malformed JSON — and that retries are counted and non-transient failures
/// are never retried.
/// </summary>
public class StediEligibilityApiClientTests
{
    private const string ActiveJson =
        "{\"meta\":{\"traceId\":\"trace-abc\"},\"planStatus\":[{\"statusCode\":\"1\"}]," +
        "\"benefitsInformation\":[{\"code\":\"1\",\"name\":\"Health Benefit Plan Coverage\"}]}";

    private static StediEligibilityApiClient NewClient(StubHttpMessageHandler handler, int maxRetries = 2)
    {
        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            MaxRetries = maxRetries
        });
        return new StediEligibilityApiClient(
            new StubHttpClientFactory(handler),
            options,
            NullLogger<StediEligibilityApiClient>.Instance,
            timeProvider: null,
            delay: (_, _) => Task.CompletedTask);
    }

    private static StediEligibilityRequestDto Request() => new()
    {
        TradingPartnerServiceId = "60054",
        Subscriber = new StediSubscriberDto { MemberId = "M1" }
    };

    [Fact]
    public async Task Success_ReturnsParsedResponse_WithTraceIdAndAuthHeader()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var client = NewClient(handler);

        var result = await client.SendEligibilityAsync(Request(), CancellationToken.None);

        result.Response.PlanStatus.Should().ContainSingle();
        result.RetryCount.Should().Be(0);
        result.ExternalTransactionId.Should().Be("trace-abc");

        handler.Requests[0].Headers.TryGetValues("Authorization", out var auth).Should().BeTrue();
        auth!.Single().Should().Be("test-key");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/eligibility/v3");
    }

    [Fact]
    public async Task Unauthorized_ThrowsAuthentication_NoRetry()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var client = NewClient(handler);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.Authentication);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Forbidden_ThrowsAuthorization_NoRetry()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Forbidden);
        var client = NewClient(handler);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.Authorization);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task BadRequest_ThrowsValidation_NoRetry()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest);
        var client = NewClient(handler);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RateLimited_ThenSuccess_Retries()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, r => r.Headers.TryAddWithoutValidation("Retry-After", "1"))
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var client = NewClient(handler);

        var result = await client.SendEligibilityAsync(Request(), CancellationToken.None);

        result.RetryCount.Should().Be(1);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RateLimited_Exhausted_ThrowsWithRetryCount()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueStatus(HttpStatusCode.TooManyRequests);
        var client = NewClient(handler, maxRetries: 1);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<StediApiException>()).Which;
        ex.Category.Should().Be(GatewayErrorCategory.RateLimited);
        ex.RetryCount.Should().Be(1);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ServerError_ThenSuccess_Retries()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var client = NewClient(handler);

        var result = await client.SendEligibilityAsync(Request(), CancellationToken.None);

        result.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Timeout_IsTransient_AndRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new TaskCanceledException("timeout"))
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var client = NewClient(handler);

        var result = await client.SendEligibilityAsync(Request(), CancellationToken.None);

        result.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task TransientNetworkError_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new HttpRequestException("connection reset"))
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var client = NewClient(handler);

        var result = await client.SendEligibilityAsync(Request(), CancellationToken.None);

        result.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task MalformedJson_ThrowsMalformedResponse_NoRetry()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, "{ this is not json");
        var client = NewClient(handler);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<StediApiException>())
            .Which.Category.Should().Be(GatewayErrorCategory.MalformedResponse);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ServerError_Exhausted_ThrowsServiceUnavailable_WithRetryCount()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError);
        var client = NewClient(handler, maxRetries: 2);

        var act = () => client.SendEligibilityAsync(Request(), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<StediApiException>()).Which;
        ex.Category.Should().Be(GatewayErrorCategory.ServiceUnavailable);
        ex.RetryCount.Should().Be(2);
        handler.CallCount.Should().Be(3);
    }
}
