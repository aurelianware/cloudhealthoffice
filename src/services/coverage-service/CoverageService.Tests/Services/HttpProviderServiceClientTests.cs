using System.Net;
using System.Net.Http;
using CoverageService.Services;
using Microsoft.Extensions.Options;

namespace CoverageService.Tests.Services;

/// <summary>
/// Only a 404 (or an unreachable provider-service) means "no such provider".
/// A refused token or permission, or a server error, must not turn into
/// PROVIDER_NOT_FOUND.
/// </summary>
public class HttpProviderServiceClientTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private static HttpProviderServiceClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") },
            Options.Create(new ProviderServiceOptions { BaseUrl = "http://provider-service" }));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RefusalOrServerError_Throws_InsteadOfNotFound(HttpStatusCode status)
    {
        var client = Client(new StubHandler(() => new HttpResponseMessage(status)));

        var act = () => client.GetByNpiAsync("t1", "1234567890");

        (await act.Should().ThrowAsync<ProviderDirectoryUnavailableException>())
            .Which.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task NotFound_ReturnsNull()
    {
        var client = Client(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetByNpiAsync("t1", "1234567890")).Should().BeNull();
    }

    [Fact]
    public async Task Unreachable_ReturnsNull()
    {
        var client = Client(new UnreachableHandler());

        (await client.GetByNpiAsync("t1", "1234567890")).Should().BeNull();
    }
}
