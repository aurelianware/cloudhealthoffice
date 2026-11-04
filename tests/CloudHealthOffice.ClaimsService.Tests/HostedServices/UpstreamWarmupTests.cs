using System.Net;
using ClaimsService.HostedServices;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.HostedServices;

/// <summary>
/// Upstream warm-up (#1824): readiness stays closed until each upstream has
/// answered from this pod, and opens anyway after the wait runs out.
/// </summary>
public class UpstreamWarmupTests
{
    private static readonly Uri Coverage = new("http://coverage-service");
    private static readonly Uri BenefitPlan = new("http://benefit-plan-service:8080");

    [Fact]
    public async Task Readiness_stays_closed_until_every_upstream_answers_twice_in_a_row()
    {
        // coverage-service fails its first two probes, then answers.
        var handler = new ScriptedHandler(uri =>
            uri.Host == Coverage.Host ? ScriptedHandler.FailThenOk(2) : ScriptedHandler.Ok);
        var state = new UpstreamWarmupState();
        var service = Create(handler, state, new UpstreamWarmupOptions { Enabled = true, Interval = TimeSpan.FromMilliseconds(5) },
            new UpstreamWarmupTarget("CoverageService", Coverage),
            new UpstreamWarmupTarget("BenefitPlanService", BenefitPlan));
        var check = new UpstreamWarmupHealthCheck(state);

        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.Complete.Should().BeTrue();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
        // 2 failures + 2 consecutive successes for coverage; 2 successes for benefit-plan.
        handler.Calls(Coverage.Host).Should().Be(4);
        handler.Calls(BenefitPlan.Host).Should().Be(2);
        handler.Paths.Should().OnlyContain(p => p == "/health/live");
    }

    [Fact]
    public async Task A_success_between_failures_does_not_count_as_warm()
    {
        var answers = new Queue<bool>([true, false, true, true]);
        var handler = new ScriptedHandler(_ => () => answers.Dequeue());
        var state = new UpstreamWarmupState();
        var service = Create(handler, state, new UpstreamWarmupOptions { Enabled = true, Interval = TimeSpan.FromMilliseconds(5) },
            new UpstreamWarmupTarget("CoverageService", Coverage));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.Complete.Should().BeTrue();
        handler.Calls(Coverage.Host).Should().Be(4);
    }

    [Fact]
    public async Task Readiness_opens_after_the_maximum_wait_when_an_upstream_never_answers()
    {
        var handler = new ScriptedHandler(_ => () => throw new HttpRequestException("connection refused"));
        var state = new UpstreamWarmupState();
        var service = Create(handler, state,
            new UpstreamWarmupOptions { Enabled = true, Interval = TimeSpan.FromMilliseconds(5), MaxWait = TimeSpan.FromMilliseconds(100) },
            new UpstreamWarmupTarget("CoverageService", Coverage));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.Complete.Should().BeTrue();
        handler.Calls(Coverage.Host).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Off_by_default_on_development_and_testing_hosts(string environment)
    {
        var handler = new ScriptedHandler(_ => () => throw new HttpRequestException("never called"));
        var state = new UpstreamWarmupState();
        var service = Create(handler, state, new UpstreamWarmupOptions(), environment,
            new UpstreamWarmupTarget("CoverageService", Coverage));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.Complete.Should().BeTrue();
        handler.Calls(Coverage.Host).Should().Be(0);
    }

    [Fact]
    public async Task On_by_default_on_production_hosts()
    {
        var handler = new ScriptedHandler(_ => ScriptedHandler.Ok);
        var state = new UpstreamWarmupState();
        var service = Create(handler, state, new UpstreamWarmupOptions { Interval = TimeSpan.FromMilliseconds(5) }, "Production",
            new UpstreamWarmupTarget("CoverageService", Coverage));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        handler.Calls(Coverage.Host).Should().Be(2);
    }

    [Fact]
    public void Targets_come_from_the_named_clients_once_per_address_plus_token_service()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("CoverageService").Returns(_ => new HttpClient { BaseAddress = Coverage });
        factory.CreateClient("BenefitPlanService").Returns(_ => new HttpClient { BaseAddress = BenefitPlan });
        factory.CreateClient("SameAsCoverage").Returns(_ => new HttpClient { BaseAddress = new Uri("http://coverage-service/api/") });
        factory.CreateClient("NoAddress").Returns(_ => new HttpClient());

        var targets = UpstreamWarmupTargets.FromClients(
            factory, ["CoverageService", "BenefitPlanService", "SameAsCoverage", "NoAddress"], "http://token-service");

        targets.Items.Select(t => t.Name).Should().Equal("CoverageService", "BenefitPlanService", "TokenService");
    }

    private static UpstreamWarmupService Create(
        HttpMessageHandler handler, UpstreamWarmupState state, UpstreamWarmupOptions options, params UpstreamWarmupTarget[] targets) =>
        Create(handler, state, options, "Production", targets);

    private static UpstreamWarmupService Create(
        HttpMessageHandler handler, UpstreamWarmupState state, UpstreamWarmupOptions options, string environment,
        params UpstreamWarmupTarget[] targets)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(UpstreamWarmupService.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        return new UpstreamWarmupService(
            new UpstreamWarmupTargets(targets), factory, state, Options.Create(options), host,
            NullLogger<UpstreamWarmupService>.Instance);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public static readonly Func<bool> Ok = () => true;

        private readonly Func<Uri, Func<bool>> _scriptFor;
        private readonly Dictionary<string, Func<bool>> _scripts = new();
        private readonly Dictionary<string, int> _calls = new();
        private readonly object _gate = new();

        public ScriptedHandler(Func<Uri, Func<bool>> scriptFor) => _scriptFor = scriptFor;

        public List<string> Paths { get; } = new();

        public int Calls(string host)
        {
            lock (_gate) return _calls.GetValueOrDefault(host);
        }

        public static Func<bool> FailThenOk(int failures)
        {
            var left = failures;
            return () => left-- <= 0;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<bool> script;
            lock (_gate)
            {
                var host = request.RequestUri!.Host;
                _calls[host] = _calls.GetValueOrDefault(host) + 1;
                Paths.Add(request.RequestUri.AbsolutePath);
                if (!_scripts.TryGetValue(host, out script!))
                    _scripts[host] = script = _scriptFor(request.RequestUri);
            }
            return Task.FromResult(new HttpResponseMessage(script() ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        }
    }
}
