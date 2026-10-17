using System.Net;
using ClaimsService.Adapters;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Tests.Http;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// claims-service's adjudication clients as Program.cs registers them, with
/// only the primary handler replaced: each keeps failing fast at its own short
/// <c>HttpClient.Timeout</c>, now enforced per attempt by the shared resilience
/// (which raises <c>HttpClient.Timeout</c> itself past its total timeout).
/// The fake dependency "takes" time on a <see cref="ManualClock"/>.
/// </summary>
public class AdjudicationClientTimeoutTests
{
    private sealed class SlowDependency(ManualClock clock, TimeSpan takes) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            clock.Advance(takes);
            ct.ThrowIfCancellationRequested(); // a timeout that fell due meanwhile cancelled the call
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static (ClaimsApiFactory Factory, HttpClient Client) Host(string clientName, ManualClock clock, HttpMessageHandler dependency)
    {
        var factory = new ClaimsApiFactory();
        var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<HttpClientFactoryOptions>(clientName,
                o => o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = dependency));
        }));
        return (factory, host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
    }

    public static TheoryData<string, int> ShortTimeoutClients => new()
    {
        { HttpMemberResolver.HttpClientName, 5 },
        // Services:BenefitPlanServiceTimeoutSeconds: 15 in appsettings.Development.json (this host), 5 in appsettings.json.
        { HttpBenefitPlanResolver.HttpClientName, 15 },
        { UpstreamClientNames.CoverageService, 5 },
        { UpstreamClientNames.AuthorizationService, 5 },
        { ClaimTenantConfigCache.HttpClientName, 5 },
        { UpstreamClientNames.ProviderService, 10 },
    };

    [Theory]
    [MemberData(nameof(ShortTimeoutClients))]
    public async Task AHungDependency_IsCutAtTheClientsOwnTimeout_OnEveryAttempt(string clientName, int timeoutSeconds)
    {
        var clock = new ManualClock();
        var dependency = new SlowDependency(clock, TimeSpan.FromSeconds(timeoutSeconds + 1));
        var (factory, client) = Host(clientName, clock, dependency);
        await using var _ = factory;

        var act = () => clock.Drive(client.GetAsync("http://dependency/api/anything"));

        (await act.Should().ThrowAsync<TaskCanceledException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
        dependency.Calls.Should().Be(3, "a GET is tried three times, each cut at the client's own timeout");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3 * (timeoutSeconds + 1) + 5),
            "the call gave up after its attempts, not at a longer default");
    }

    [Theory]
    [MemberData(nameof(ShortTimeoutClients))]
    public async Task AnAnswerJustInsideTheClientsOwnTimeout_Succeeds(string clientName, int timeoutSeconds)
    {
        var clock = new ManualClock();
        var dependency = new SlowDependency(clock, TimeSpan.FromSeconds(timeoutSeconds - 1));
        var (factory, client) = Host(clientName, clock, dependency);
        await using var _ = factory;

        var response = await clock.Drive(client.GetAsync("http://dependency/api/anything"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        dependency.Calls.Should().Be(1);
    }
}
