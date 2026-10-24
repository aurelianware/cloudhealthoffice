using System.Net;
using CoverageService.Services;

namespace CoverageService.Tests.Services;

public class HttpPanelCounterTests
{
    private sealed class Handler(Func<CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(ct));
    }

    private static HttpPanelCounter Counter(Func<CancellationToken, HttpResponseMessage> response)
        => new(new HttpClient(new Handler(response)) { BaseAddress = new Uri("http://coverage-service") });

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task FailedLookup_IsNotAnEmptyPanel(int status)
    {
        var counter = Counter(_ => new((HttpStatusCode)status));
        var act = () => counter.CurrentPanelCountAsync("tenant-1", "1234567890");
        await act.Should().ThrowAsync<PanelCountUnavailableException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task UnreadableLookup_IsNotAnEmptyPanel(string payload)
    {
        var counter = Counter(_ => new(HttpStatusCode.OK) { Content = new StringContent(payload) });
        var act = () => counter.CurrentPanelCountAsync("tenant-1", "1234567890");
        await act.Should().ThrowAsync<PanelCountUnavailableException>();
    }

    [Fact]
    public async Task MissingConfiguration_IsNotAnEmptyPanel()
    {
        var counter = new HttpPanelCounter(new HttpClient());
        var act = () => counter.CurrentPanelCountAsync("tenant-1", "1234567890");
        await act.Should().ThrowAsync<PanelCountUnavailableException>();
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{},{}]", 2)]
    public async Task ReadableRoster_ReturnsMeasuredCount(string payload, int expected)
    {
        var counter = Counter(_ => new(HttpStatusCode.OK) { Content = new StringContent(payload) });
        (await counter.CurrentPanelCountAsync("tenant-1", "1234567890")).Should().Be(expected);
    }

    [Fact]
    public async Task TransportTimeout_IsUnavailable()
    {
        var counter = Counter(_ => throw new TaskCanceledException());
        var act = () => counter.CurrentPanelCountAsync("tenant-1", "1234567890");
        await act.Should().ThrowAsync<PanelCountUnavailableException>();
    }

    [Fact]
    public async Task CallerCancellation_IsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var counter = Counter(ct => throw new OperationCanceledException(ct));
        var act = () => counter.CurrentPanelCountAsync("tenant-1", "1234567890", cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
