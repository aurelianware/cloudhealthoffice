using System.Net;

namespace CloudHealthOffice.Portal.Tests;

/// <summary>
/// A fake HTTP message handler that returns a fixed status code for all requests.
/// Used to simulate API failures so services fall back to mock data.
/// </summary>
public class FakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;

    public FakeHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_statusCode));
    }
}
