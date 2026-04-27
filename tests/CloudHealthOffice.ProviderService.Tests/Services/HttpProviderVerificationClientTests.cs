using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Coverage for <see cref="HttpProviderVerificationClient"/>'s failure
/// posture: caller cancellation propagates (worker shutdown is
/// timely), HTTP timeouts and transport failures degrade to "outage"
/// (cached scores stay put), oversized batches throw early.
/// </summary>
public class HttpProviderVerificationClientTests
{
    private static HttpProviderVerificationClient BuildClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var http = new HttpClient(new DelegateHandler(handler))
        {
            BaseAddress = new Uri("http://provider-verification-service/"),
        };
        return new HttpProviderVerificationClient(
            http, NullLogger<HttpProviderVerificationClient>.Instance);
    }

    [Fact]
    public async Task VerifyBatchAsync_propagates_caller_cancellation()
    {
        // Handler honours the caller's CancellationToken: when the
        // caller cancels, the awaited Task throws OperationCanceledException
        // and the client must rethrow rather than swallow as outage.
        var client = BuildClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = () => client.VerifyBatchAsync(new[] { "1234567890" }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerifyBatchAsync_returns_empty_on_transport_failure()
    {
        // Genuine HTTP transport failure (server unreachable / DNS
        // failure / timeout from HttpClient.Timeout) MUST degrade
        // gracefully — the projection writer preserves cached scores.
        var client = BuildClient((_, _) =>
            throw new HttpRequestException("server unreachable"));

        var result = await client.VerifyBatchAsync(new[] { "1234567890" });
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyBatchAsync_returns_empty_on_HttpClient_timeout()
    {
        // HttpClient.Timeout fires as TaskCanceledException with the
        // CancellationToken NOT signalled. That's the "outage" path.
        var client = BuildClient((_, _) =>
            throw new TaskCanceledException("HttpClient timeout"));

        var result = await client.VerifyBatchAsync(new[] { "1234567890" });
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyBatchAsync_rejects_oversized_batches()
    {
        var client = BuildClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var npis = Enumerable.Range(0, 101).Select(i => i.ToString("D10")).ToArray();
        var act = () => client.VerifyBatchAsync(npis);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task VerifyBatchAsync_short_circuits_on_empty_input()
    {
        var called = false;
        var client = BuildClient((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.VerifyBatchAsync(Array.Empty<string>());
        result.Should().BeEmpty();
        called.Should().BeFalse();
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _h;
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> h) => _h = h;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _h(request, cancellationToken);
    }
}
