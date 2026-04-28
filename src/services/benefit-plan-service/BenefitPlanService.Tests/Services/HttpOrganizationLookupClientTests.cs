using System.Net;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability 5.5 — verifies the HTTP-only contract between
/// benefit-plan-service and provider-service for Organization
/// resolution. Mirrors the failure-mode posture from
/// <see cref="HttpProviderIntegrityGate"/>: 404 and transport failures
/// surface as <c>null</c>; the caller decides policy.
/// </summary>
public sealed class HttpOrganizationLookupClientTests
{
    [Fact]
    public async Task GetOrganizationAsync_Returns_The_Organization_When_Resolved()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"organizationId\":\"net-1\",\"name\":\"Aetna PPO Florida 2025\",\"effectiveDate\":\"2025-01-01T00:00:00Z\"}");
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync("net-1");

        result.Should().NotBeNull();
        result!.OrganizationId.Should().Be("net-1");
        result.Name.Should().Be("Aetna PPO Florida 2025");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrganizationAsync_Returns_Null_On_404()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync("missing-network");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganizationAsync_Returns_Null_On_5xx_Without_Throwing()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync("flaky-network");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganizationAsync_Returns_Null_On_Transport_Failure()
    {
        var handler = FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync("unreachable");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganizationAsync_Returns_Null_When_NetworkId_Is_Empty()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.OK);
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync(string.Empty);

        result.Should().BeNull();
        // Short-circuit: no HTTP call issued for an empty id.
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetOrganizationAsync_Url_Encodes_The_NetworkId()
    {
        string? observedPath = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            observedPath = req.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"organizationId\":\"net 1\",\"name\":\"With Space\",\"effectiveDate\":\"2025-01-01T00:00:00Z\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = BuildClient(handler);

        var result = await client.GetOrganizationAsync("net 1");

        result.Should().NotBeNull();
        observedPath.Should().Be("/api/v1/networks/net%201");
    }

    private static HttpOrganizationLookupClient BuildClient(HttpMessageHandler handler)
    {
        var factory = new SingleClientFactory(handler);
        return new HttpOrganizationLookupClient(factory, NullLogger<HttpOrganizationLookupClient>.Instance);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://provider-service:8080/"),
        };
    }
}
