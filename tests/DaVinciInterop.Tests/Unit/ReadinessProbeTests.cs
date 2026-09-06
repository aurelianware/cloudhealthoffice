using System.Net;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Readiness must be deterministic and bounded: a dead external implementation
/// fails with a diagnostic naming the furthest stage it reached, never a hang and
/// never a guessed sleep.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ReadinessProbeTests
{
    private static readonly ExternalServiceDefinition Payer = InteropVersions.Load().Target("br-payer");

    [Fact]
    public async Task Metadata_readiness_is_reached_once_a_capability_statement_is_served()
    {
        using var http = StubClient(_ => Json(HttpStatusCode.OK, CapabilityStatementJson));
        var probe = new ReadinessProbe(http);

        var outcome = await probe.WaitAsync(
            Payer, ReadinessStage.FhirMetadataAvailable, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));

        outcome.IsReady.Should().BeTrue();
        outcome.ReachedStage.Should().Be(ReadinessStage.FhirMetadataAvailable);
        outcome.CapabilityStatement!.FhirVersion.Should().NotBeNull();
    }

    [Fact]
    public async Task An_http_endpoint_that_never_serves_fhir_stops_at_HttpReachable()
    {
        using var http = StubClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>starting…</html>"),
        });
        var probe = new ReadinessProbe(http);

        var outcome = await probe.WaitAsync(
            Payer, ReadinessStage.FhirMetadataAvailable, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(10));

        outcome.IsReady.Should().BeFalse();
        outcome.ReachedStage.Should().Be(ReadinessStage.HttpReachable);
        outcome.Diagnostic.Should().Contain("furthest stage: HttpReachable");
    }

    [Fact]
    public async Task A_container_that_never_accepts_connections_times_out_with_a_diagnostic()
    {
        using var http = StubClient(_ => throw new HttpRequestException("connection refused"));
        var probe = new ReadinessProbe(http);

        var outcome = await probe.WaitAsync(
            Payer, ReadinessStage.FhirMetadataAvailable, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10));

        outcome.IsReady.Should().BeFalse();
        outcome.Diagnostic.Should().Contain(Payer.Name);
        outcome.Diagnostic.Should().Contain("connection refused");
        outcome.Attempts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Application_readiness_additionally_requires_cds_hooks_discovery_to_answer()
    {
        var discoveryCalls = 0;
        using var http = StubClient(request =>
        {
            if (request.RequestUri!.ToString().Contains("cds-services"))
            {
                discoveryCalls++;
                return discoveryCalls < 2
                    ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                    : Json(HttpStatusCode.OK, """{"services":[{"hook":"order-sign","id":"order-sign-crd"}]}""");
            }

            return Json(HttpStatusCode.OK, CapabilityStatementJson);
        });
        var probe = new ReadinessProbe(http);

        var outcome = await probe.WaitAsync(
            Payer, ReadinessStage.ApplicationReady, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));

        outcome.IsReady.Should().BeTrue();
        outcome.ReachedStage.Should().Be(ReadinessStage.ApplicationReady);
        discoveryCalls.Should().BeGreaterThan(1, "the probe polls rather than assuming the first answer is final");
    }

    private const string CapabilityStatementJson =
        """{"resourceType":"CapabilityStatement","status":"active","date":"2026-01-01","kind":"instance","fhirVersion":"4.0.1","format":["application/fhir+json"],"rest":[{"mode":"server"}]}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/fhir+json") };

    private static HttpClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
