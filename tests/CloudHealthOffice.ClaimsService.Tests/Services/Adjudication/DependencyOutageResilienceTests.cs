using System.Net;
using ClaimsService.Models;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// A dependency outage seen through claims-service's real HTTP client and
/// the shared client resilience: a 503 is retried; a sustained outage opens
/// the circuit, after which calls fail fast; and the claim pends as "check
/// could not run" — never denied, never overridable by an examiner approval —
/// decided by the typed <see cref="ClaimAdjudicationStageResult.CheckUnavailable"/>
/// flag, not by matching the reason text.
/// </summary>
public class DependencyOutageResilienceTests
{
    private const string Tenant = "tenant-a";

    private sealed class BenefitPlanService : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _script = new();
        public HttpStatusCode Otherwise { get; set; } = HttpStatusCode.ServiceUnavailable;
        public int Calls;

        public BenefitPlanService Then(params HttpStatusCode[] codes)
        {
            foreach (var c in codes) _script.Enqueue(c);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var status = _script.Count > 0 ? _script.Dequeue() : Otherwise;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? """{"npi":"1234567890","passed":true,"isExcluded":false,"requiresManualReview":false}"""
                    : "{}"),
            });
        }
    }

    private static ProviderIntegrityStage Stage(BenefitPlanService downstream, Action<ChoResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoServiceClientResilience();
        services.AddHttpClient(UpstreamClientNames.BenefitPlanService, c => c.BaseAddress = new Uri("http://benefit-plan-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => downstream)
            .ConfigureChoResilience(o =>
            {
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                o.CircuitMinimumThroughput = 3;
                o.CircuitBreakDuration = TimeSpan.FromMinutes(1);
                configure?.Invoke(o);
            });
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new ProviderIntegrityStage(
            new HttpProviderIntegrityClient(factory, NullLogger<HttpProviderIntegrityClient>.Instance),
            NullLogger<ProviderIntegrityStage>.Instance);
    }

    private static ClaimAdjudicationContext Context() => new()
    {
        TenantId = Tenant,
        ClaimVersionId = "v-1",
        Claim = new AdapterClaim
        {
            TenantId = Tenant, Id = "claim-1", ClaimNumber = "C-1", ClaimVersionId = "v-1",
            BillingProviderNPI = "1234567890", RenderingProviderNPI = string.Empty,
            ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            BenefitPlanId = "plan-1",
        },
    };

    [Fact]
    public async Task ADownstream503_IsRetried_AndTheCheckPasses()
    {
        var downstream = new BenefitPlanService().Then(HttpStatusCode.ServiceUnavailable);
        downstream.Otherwise = HttpStatusCode.OK;

        var result = await Stage(downstream).ExecuteAsync(Context(), CancellationToken.None);

        result.Outcome.Should().Be(ClaimAdjudicationOutcome.Pass);
        downstream.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ASustainedOutage_OpensTheCircuit_AndTheClaimPends_AsACheckThatCouldNotRun()
    {
        var downstream = new BenefitPlanService();
        var stage = Stage(downstream, o => o.MaxRetryAttempts = 0);

        for (var i = 0; i < 3; i++)
            (await stage.ExecuteAsync(Context(), CancellationToken.None)).Outcome.Should().Be(ClaimAdjudicationOutcome.Pend);
        var callsBeforeOpen = downstream.Calls;

        var ctx = Context();
        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        downstream.Calls.Should().Be(callsBeforeOpen, "the circuit is open: the dependency is not called");
        result.Outcome.Should().Be(ClaimAdjudicationOutcome.Pend, "an outage is never a denial");
        result.CheckUnavailable.Should().BeTrue();
        ctx.IsCheckUnavailable(ctx.PendDetails!.PendReason).Should().BeTrue();
        ClaimAdjudicationOrchestrator.AnyCheckFailed(ctx).Should().BeTrue();
    }

    [Fact]
    public void WhetherACheckCouldNotRun_IsTheFlag_NotTheWording()
    {
        // A finding whose text happens to read like an outage is still a finding...
        var finding = Context();
        finding.PendDetails = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: Provider integrity check could not be reached." };
        finding.StageResults.Add(ClaimAdjudicationStageResult.Pend("ProviderIntegrity", finding.PendDetails.PendReason));
        ClaimAdjudicationOrchestrator.AnyCheckFailed(finding).Should().BeFalse();

        // ...and an outage reworded is still an outage.
        var outage = Context();
        outage.PendDetails = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: the integrity service is down." };
        outage.StageResults.Add(ClaimAdjudicationStageResult.Pend("ProviderIntegrity", outage.PendDetails.PendReason)
            .AsCheckUnavailable(outage, outage.PendDetails.PendReason));
        ClaimAdjudicationOrchestrator.AnyCheckFailed(outage).Should().BeTrue();
    }
}
