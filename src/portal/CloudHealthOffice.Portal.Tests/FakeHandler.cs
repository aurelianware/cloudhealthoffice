using System.Net;
using System.Text;

namespace CloudHealthOffice.Portal.Tests;

/// <summary>
/// A fake HTTP message handler that returns a fixed status code for all requests.
/// Used to simulate API failures so services fall back to mock data.
/// </summary>
public class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public List<HttpRequestMessage> CapturedRequests { get; } = new();

    public List<string> CapturedUrls =>
        CapturedRequests.Select(r => r.RequestUri?.AbsoluteUri ?? "").ToList();

    public FakeHandler(HttpStatusCode statusCode)
        : this(_ => new HttpResponseMessage(statusCode))
    {
    }

    public FakeHandler(HttpStatusCode statusCode, string responseBody)
        : this(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            response.Content = new StringContent(responseBody, Encoding.UTF8, "application/json");
            return response;
        })
    {
    }

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequests.Add(request);
        return Task.FromResult(_handler(request));
    }
}
