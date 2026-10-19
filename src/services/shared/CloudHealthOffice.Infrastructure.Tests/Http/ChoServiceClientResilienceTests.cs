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

    // ── Timeouts follow the client's own HttpClient.Timeout ──────────────

    private static ServiceProvider ClientWithItsOwnTimeout(
        ManualClock clock, HttpMessageHandler downstream, TimeSpan ownTimeout, Action<ChoResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoServiceClientResilience();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("vendor", c =>
            {
                c.BaseAddress = new Uri("http://vendor/");
                c.Timeout = ownTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => downstream)
            .ConfigureChoResilience(o =>
            {
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                configure?.Invoke(o);
            });
        return services.BuildServiceProvider();
    }

    /// <summary>Takes its time on the clock, then answers 200 unless a timeout cancelled it meanwhile.</summary>
    private sealed class Slow(ManualClock clock, TimeSpan takes) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            clock.Advance(takes);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task AClientsOwnTimeout_IsItsAttemptTimeout_SoACallShorterThanItSucceeds()
    {
        var clock = new ManualClock();
        var slow = new Slow(clock, TimeSpan.FromSeconds(45));
        using var provider = ClientWithItsOwnTimeout(clock, slow, TimeSpan.FromSeconds(60));

        var response = await clock.Drive(provider.GetRequiredService<IHttpClientFactory>().CreateClient("vendor").GetAsync("x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        slow.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AClientsOwnTimeout_StillCutsAnAttemptThatTakesLonger()
    {
        var clock = new ManualClock();
        var slow = new Slow(clock, TimeSpan.FromSeconds(6));
        using var provider = ClientWithItsOwnTimeout(clock, slow, TimeSpan.FromSeconds(5));

        var act = () => clock.Drive(provider.GetRequiredService<IHttpClientFactory>().CreateClient("vendor").GetAsync("x"));

        (await act.Should().ThrowAsync<TaskCanceledException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
        slow.Calls.Should().Be(3, "each of the three attempts is cut at the client's 5 s");
    }

    [Fact]
    public void TheTotalTimeout_CoversEveryAttempt_AndHttpClientTimeoutIsRaisedPastIt()
    {
        using var provider = ClientWithItsOwnTimeout(new ManualClock(), new Downstream(), TimeSpan.FromSeconds(120));

        var timeouts = provider.GetRequiredService<ChoResiliencePipelines>().Timeouts("vendor")!;
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("vendor");

        timeouts.OwnTimeout.Should().Be(TimeSpan.FromSeconds(120));
        timeouts.Attempt.Should().Be(TimeSpan.FromSeconds(120));
        timeouts.Total.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120 * 3));
        client.Timeout.Should().BeGreaterThan(timeouts.Total!.Value, "the pipeline's timeouts govern, not HttpClient.Timeout");
    }

    [Fact]
    public void ExplicitTimeouts_WinOverTheClientsOwn()
    {
        using var provider = ClientWithItsOwnTimeout(new ManualClock(), new Downstream(), TimeSpan.FromSeconds(120), o =>
        {
            o.AttemptTimeout = TimeSpan.FromSeconds(7);
            o.TotalTimeout = TimeSpan.FromSeconds(9);
        });

        var timeouts = provider.GetRequiredService<ChoResiliencePipelines>().Timeouts("vendor")!;

        timeouts.Attempt.Should().Be(TimeSpan.FromSeconds(7));
        timeouts.Total.Should().Be(TimeSpan.FromSeconds(9));
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("vendor").Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AClientWithoutATimeoutOfItsOwn_KeepsTheDefaults()
    {
        var services = new ServiceCollection();
        services.AddChoServiceClientResilience();
        services.AddHttpClient("plain");
        using var provider = services.BuildServiceProvider();

        var timeouts = provider.GetRequiredService<ChoResiliencePipelines>().Timeouts("plain")!;

        timeouts.OwnTimeout.Should().BeNull();
        timeouts.Attempt.Should().Be(ChoResilienceOptions.DefaultAttemptTimeout);
        timeouts.Total.Should().BeGreaterThanOrEqualTo(ChoResilienceOptions.DefaultTotalTimeout);
    }

    [Fact]
    public void AClientWithResilienceTurnedOff_KeepsItsOwnHttpClientTimeout()
    {
        var services = new ServiceCollection();
        services.AddChoServiceClientResilience();
        services.AddHttpClient("own", c => c.Timeout = TimeSpan.FromSeconds(42)).WithoutChoResilience();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient("own").Timeout.Should().Be(TimeSpan.FromSeconds(42));
    }
}
