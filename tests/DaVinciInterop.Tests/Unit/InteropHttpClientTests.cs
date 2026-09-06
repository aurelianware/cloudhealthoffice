using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The HTTP layer is what an interop failure is diagnosed from, so what it records
/// matters as much as what it sends: the exchange must stay legible, and no
/// credential may survive into a captured artifact.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class InteropHttpClientTests
{
    [Fact]
    public async Task A_fhir_response_is_parsed_with_the_library_cho_runs()
    {
        using var client = ClientReturning(HttpStatusCode.OK,
            """{"resourceType":"CapabilityStatement","status":"active","fhirVersion":"4.0.1"}""");

        var response = await client.GetFhirAsync("http://127.0.0.1:18081/fhir/metadata");

        response.IsSuccess.Should().BeTrue();
        response.As<CapabilityStatement>()!.FhirVersion.Should().Be(FHIRVersion.N4_0_1);
        response.Interaction.ResponseResourceType.Should().Be("CapabilityStatement");
    }

    [Fact]
    public async Task An_operation_outcome_is_summarized_so_a_failure_says_what_upstream_objected_to()
    {
        using var client = ClientReturning(HttpStatusCode.BadRequest,
            """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"invalid","diagnostics":"Claim.item.category is required for item 1"}]}""");

        var response = await client.GetFhirAsync("http://127.0.0.1:18081/fhir/Claim/$submit");

        response.IsSuccess.Should().BeFalse();
        response.OperationOutcome.Should().NotBeNull();
        response.Interaction.OperationOutcomeIssues
            .Should().ContainSingle().Which.Should().Contain("Claim.item.category is required");
    }

    [Fact]
    public async Task A_non_fhir_body_is_recorded_rather_than_treated_as_a_harness_failure()
    {
        using var client = ClientReturning(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>", "text/html");

        var response = await client.GetFhirAsync("http://127.0.0.1:18081/fhir/metadata");

        response.Resource.Should().BeNull();
        response.Interaction.StatusCode.Should().Be(502);
        response.Body.Should().Contain("Bad Gateway");
    }

    [Fact]
    public async Task A_bearer_token_never_reaches_a_recorded_interaction()
    {
        using var client = ClientReturning(HttpStatusCode.OK, """{"resourceType":"Bundle","type":"collection"}""");
        client.UseBearerToken("super-secret-token");

        var response = await client.GetFhirAsync("http://127.0.0.1:18081/fhir/Claim");

        response.Interaction.RequestHeaders["Authorization"].Should().Be(Redaction.Placeholder);
        response.Interaction.RequestHeaders.Values.Should().NotContain(v => v.Contains("super-secret-token"));
    }

    [Fact]
    public async Task Request_and_response_bodies_are_captured_for_the_evidence_package()
    {
        using var client = ClientReturning(HttpStatusCode.OK, """{"resourceType":"Bundle","type":"collection"}""");

        var response = await client.PostFhirAsync(
            "http://127.0.0.1:18081/fhir/Claim/$submit",
            SyntheticInteropData.AsSubmitParameters(SyntheticInteropData.PasRequestBundle(DateTimeOffset.UtcNow)));

        response.Interaction.RequestArtifact.Should().Be("requests/001-post.json");
        response.Interaction.ResponseArtifact.Should().Be("responses/001-200.json");
        client.CapturedBodies[response.Interaction.RequestArtifact!]
            .Should().Contain(SyntheticInteropData.MemberId);
    }

    [Fact]
    public async Task A_request_that_never_reaches_the_target_is_recorded_before_it_throws()
    {
        using var client = new InteropHttpClient(
            "HL7-DaVinci/br-payer",
            TimeSpan.FromSeconds(5),
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))));

        var act = () => client.GetFhirAsync("http://127.0.0.1:18081/fhir/metadata");

        await act.Should().ThrowAsync<InteropTransportException>().WithMessage("*connection refused*");
        client.Interactions.Should().ContainSingle()
            .Which.TransportError.Should().Contain("connection refused");
    }

    [Fact]
    public async Task Cds_hooks_discovery_is_returned_parsed_alongside_the_recorded_interaction()
    {
        using var client = ClientReturning(HttpStatusCode.OK,
            """{"services":[{"hook":"order-sign","id":"order-sign-crd","extension":{"davinci-crd.version":["2.2"]}}]}""",
            "application/json");

        var (discovery, response) = await client.GetCdsHooksDiscoveryAsync("http://127.0.0.1:18081/cds-services");

        response.IsSuccess.Should().BeTrue();
        discovery!.Services.Should().ContainSingle();
        discovery.Services[0].Hook.Should().Be("order-sign");
        discovery.Services[0].AdvertisedCrdVersions.Should().BeEquivalentTo(["2.2"]);
    }

    private static InteropHttpClient ClientReturning(
        HttpStatusCode status,
        string body,
        string contentType = "application/fhir+json") =>
        new("HL7-DaVinci/br-payer",
            TimeSpan.FromSeconds(5),
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
            })));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
