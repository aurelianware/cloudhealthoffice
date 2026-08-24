using System.Net;
using System.Net.Http.Headers;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Test <see cref="HttpMessageHandler"/> that returns a queued sequence of
/// responses (or produces them from a factory) and records every request it
/// received, so tests can drive Stedi HTTP behaviour without a real network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();
    public int CallCount { get; private set; }

    public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public StubHttpMessageHandler EnqueueJson(HttpStatusCode status, string json)
        => Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    public StubHttpMessageHandler EnqueueStatus(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
        => Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            configure?.Invoke(response);
            return response;
        });

    public StubHttpMessageHandler EnqueueThrow(Exception ex)
        => Enqueue(_ => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("StubHttpMessageHandler has no queued response.");
        }

        var responder = _responders.Count == 1 ? _responders.Peek() : _responders.Dequeue();
        return responder(request);
    }
}

/// <summary>Minimal <see cref="IHttpClientFactory"/> that hands out a client wired to a stub handler.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly Uri? _baseAddress;

    public StubHttpClientFactory(HttpMessageHandler handler, string? baseAddress = "https://healthcare.test")
    {
        _handler = handler;
        _baseAddress = baseAddress is null ? null : new Uri(baseAddress);
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
    {
        BaseAddress = _baseAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static AuthenticationHeaderValue? Auth(HttpRequestMessage request) => request.Headers.Authorization;
}
