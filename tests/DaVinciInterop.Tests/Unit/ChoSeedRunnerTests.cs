using System.Net;
using DaVinciInterop.Tests.Harness;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

public sealed class ChoSeedRunnerTests
{
    [Fact]
    public async Task The_seed_waits_until_a_service_that_resets_connections_is_live()
    {
        // A container that has started but not yet bound its port answers the
        // way fhir-service did in run 40937514337: the connection is reset.
        var handler = new Scripted(
            _ => throw new HttpRequestException("read ECONNRESET"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await ChoSeedRunner.WaitUntilLiveAsync(
            http, ["http://127.0.0.1:18023"], TimeSpan.FromSeconds(30), TimeSpan.Zero);

        handler.Requests.Should().Equal(Enumerable.Repeat("http://127.0.0.1:18023/health/live", 3));
    }

    [Fact]
    public async Task A_service_that_never_comes_up_stops_the_scenario_with_its_last_answer()
    {
        var handler = new Scripted(_ => throw new HttpRequestException("read ECONNRESET"));
        using var http = new HttpClient(handler);

        var wait = () => ChoSeedRunner.WaitUntilLiveAsync(
            http, ["http://127.0.0.1:18023/"], TimeSpan.Zero, TimeSpan.Zero);

        (await wait.Should().ThrowAsync<InteropEnvironmentException>())
            .Which.Message.Should().Contain("http://127.0.0.1:18023/health/live").And.Contain("ECONNRESET");
    }

    /// <summary>Answers each request with the next step; the last step repeats.</summary>
    private sealed class Scripted(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var step = steps[Math.Min(_next++, steps.Length - 1)];
            return Task.FromResult(step(request));
        }
    }
}
