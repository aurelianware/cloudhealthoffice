using System.Net;
using CloudHealthOffice.Infrastructure.Http;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace CloudHealthOffice.Infrastructure.Tests.Http;

/// <summary>
/// The default resilience every factory-built client gets
/// (<see cref="ChoServiceClientResilience.AddChoServiceClientResilience"/>):
/// transient failures of idempotent requests are retried, a POST is not, a
/// sustained outage opens the circuit, and the failures surface as the
/// exceptions callers already handle.
/// </summary>
public class ChoServiceClientResilienceTests
{
    private sealed class Downstream : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _script = new();
        public HttpStatusCode Otherwise { get; set; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; set; }
        public int Calls;

        public Downstream Then(params HttpStatusCode[] codes)
        {
            foreach (var c in codes) _script.Enqueue(c);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            var status = _script.Count > 0 ? _script.Dequeue() : Otherwise;
            return new HttpResponseMessage(status);
        }
    }

    private static HttpClient Client(Downstream downstream, Action<ChoResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoServiceClientResilience();
        services.AddHttpClient("member-service", c => c.BaseAddress = new Uri("http://member-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => downstream)
            .ConfigureChoResilience(o =>
            {
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                o.CircuitMinimumThroughput = 4;
                o.CircuitBreakDuration = TimeSpan.FromSeconds(30);
                configure?.Invoke(o);
            });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("member-service");
    }

    [Fact]
    public async Task ADownstream503OnAGet_IsRetried_AndThenSucceeds()
    {
        var downstream = new Downstream().Then(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);

        var response = await Client(downstream).GetAsync("api/members/M1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.Calls.Should().Be(3);
    }

    [Fact]
    public async Task APost_IsNotRetried()
    {
        var downstream = new Downstream().Then(HttpStatusCode.ServiceUnavailable);

        var response = await Client(downstream).PostAsync("api/members", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.Calls.Should().Be(1);
    }

    [Fact]
    public async Task APostWithAnIdempotencyKey_IsRetried()
    {
        var downstream = new Downstream().Then(HttpStatusCode.ServiceUnavailable);
        var request = new HttpRequestMessage(HttpMethod.Post, "api/members") { Content = new StringContent("{}") };
        request.Headers.Add(ChoServiceClientResilience.IdempotencyKeyHeader, "k-1");

        var response = await Client(downstream).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ANonTransientFailure_IsNotRetried()
    {
        var downstream = new Downstream().Then(HttpStatusCode.NotFound);

        var response = await Client(downstream).GetAsync("api/members/M404");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        downstream.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ASustainedOutage_OpensTheCircuit_AndCallsFailFastAsA503HttpRequestException()
    {
        var downstream = new Downstream { Otherwise = HttpStatusCode.ServiceUnavailable };
        var client = Client(downstream, o => o.MaxRetryAttempts = 0);

        for (var i = 0; i < 4; i++)
            (await client.GetAsync("api/members/M1")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var callsBefore = downstream.Calls;

        var act = () => client.GetAsync("api/members/M1");

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ex.InnerException.Should().BeOfType<BrokenCircuitException>();
        downstream.Calls.Should().Be(callsBefore, "an open circuit does not call the dependency");
    }

    [Fact]
    public async Task A429_IsRetried_ForAClientThatRetriesNonIdempotentCalls()
    {
        // The Anthropic Messages API shape: a POST whose repeat has no side effect.
        var downstream = new Downstream().Then(HttpStatusCode.TooManyRequests, (HttpStatusCode)529);

        var response = await Client(downstream, o => o.RetryNonIdempotent = true)
            .PostAsync("v1/messages", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.Calls.Should().Be(3);
    }

    [Fact]
    public async Task AClientWithResilienceTurnedOff_PassesThrough()
    {
        var downstream = new Downstream().Then(HttpStatusCode.ServiceUnavailable);

        var response = await Client(downstream, o => o.Enabled = false).GetAsync("api/members/M1");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnAttemptThatTakesTooLong_TimesOut_AsATaskCanceledExceptionWithATimeout()
    {
        var downstream = new Downstream { Delay = TimeSpan.FromSeconds(5) };
        var client = Client(downstream, o =>
        {
            o.AttemptTimeout = TimeSpan.FromMilliseconds(50);
            o.MaxRetryAttempts = 0;
        });

        var act = () => client.GetAsync("api/members/M1");

        (await act.Should().ThrowAsync<TaskCanceledException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
    }
}
