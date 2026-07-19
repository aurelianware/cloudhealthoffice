using System.Net;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters;

/// <summary>
/// Test helper: returns scripted responses for each HTTP call. Tracks the
/// number of requests so we can assert on cache hits.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public int RequestCount { get; private set; }
    public string? LastClientName { get; set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static FakeHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    public static FakeHttpMessageHandler Status(HttpStatusCode status)
        => new(_ => new HttpResponseMessage(status));

    public static FakeHttpMessageHandler Throw(Exception exception)
        => new(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(_responder(request));
    }
}

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that hands out an
/// <see cref="HttpClient"/> wrapping the supplied handler.
/// </summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public StubHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
