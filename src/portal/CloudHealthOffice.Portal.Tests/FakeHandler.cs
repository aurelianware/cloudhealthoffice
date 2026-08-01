using System.Net;
using System.Text;

namespace CloudHealthOffice.Portal.Tests;

/// <summary>
/// A fake HTTP message handler for use in tests. Supports returning a fixed status code,
/// a fixed status code with a JSON body, or a per-request response via a delegate factory.
/// Captures all outgoing requests in <see cref="CapturedRequests"/> for assertion support.
/// </summary>
public class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    private readonly List<HttpRequestMessage> _capturedRequests = new();

    public IReadOnlyList<HttpRequestMessage> CapturedRequests => _capturedRequests;

    public IReadOnlyList<string> CapturedUrls =>
        _capturedRequests.Select(r => r.RequestUri?.AbsoluteUri ?? "").ToList();

    public FakeHandler(HttpStatusCode statusCode)
        : this(_ => new HttpResponseMessage(statusCode))
    {
    }

    public FakeHandler(HttpStatusCode statusCode, string responseBody)
        : this(request =>
        {
            var response = new HttpResponseMessage(statusCode);
            var body = request.RequestUri?.AbsolutePath.EndsWith("/audit-timeline", StringComparison.Ordinal) == true
                ? "[]"
                : responseBody;
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        })
    {
    }

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }

        _capturedRequests.Add(request);
        return Task.FromResult(_handler(request));
    }
}
