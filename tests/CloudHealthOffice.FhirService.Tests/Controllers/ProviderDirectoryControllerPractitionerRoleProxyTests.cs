using System.Net;
using System.Text;
using FhirService.Controllers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Capability 5.8 — proxy-shape coverage for the rewritten
/// PractitionerRole endpoints in
/// <see cref="ProviderDirectoryController"/>. Mirrors the 5.7
/// Practitioner proxy tests: the controller no longer talks to NPPES on
/// the PractitionerRole path; it issues a single GET to the typed
/// <c>ProviderService</c> HttpClient and passes the response through.
/// </summary>
public class ProviderDirectoryControllerPractitionerRoleProxyTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _providerServiceHandler = new();
    private readonly RecordingHandler _verificationHandler = new();
    private readonly RecordingHandler _nppesHandler = new();
    private readonly ProviderDirectoryController _controller;

    public ProviderDirectoryControllerPractitionerRoleProxyTests()
    {
        _factory.Setup(f => f.CreateClient("ProviderService"))
            .Returns(() => new HttpClient(_providerServiceHandler)
            {
                BaseAddress = new Uri("http://provider-service.test/")
            });
        _factory.Setup(f => f.CreateClient("ProviderVerificationService"))
            .Returns(() => new HttpClient(_verificationHandler)
            {
                BaseAddress = new Uri("http://provider-verification-service.test/")
            });
        _factory.Setup(f => f.CreateClient("NppesApi"))
            .Returns(() => new HttpClient(_nppesHandler)
            {
                BaseAddress = new Uri("http://nppes.test/")
            });

        _controller = new ProviderDirectoryController(
            _factory.Object,
            NullLogger<ProviderDirectoryController>.Instance);
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    [Fact]
    public async Task ReadPractitionerRole_forwards_composite_id_to_provider_service()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"PractitionerRole\",\"id\":\"1234567890-1-20240101-net\"}"));

        var compositeId = "1234567890-1-20240101-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var result = await _controller.ReadPractitionerRole(compositeId, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"PractitionerRole\"");

        var req = _providerServiceHandler.Calls.Single();
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be($"/fhir/PractitionerRole/{compositeId}");
    }

    [Fact]
    public async Task ReadPractitionerRole_passes_404_OperationOutcome_through()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.NotFound,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));

        var result = await _controller.ReadPractitionerRole("not-found-id", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
        content.Content.Should().Contain("not-found");
    }

    [Fact]
    public async Task ReadPractitionerRole_translates_upstream_5xx_to_502_OperationOutcome()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "internal upstream noise that must NOT leak"));

        var result = await _controller.ReadPractitionerRole(
            "1234567890-1-20240101-network-a", default);
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue.Single().Diagnostics.Should().NotContain("internal upstream noise");
        // Diagnostics labels the resource so operators can distinguish
        // PractitionerRole upstream failures from Practitioner ones.
        outcome.Issue.Single().Diagnostics.Should().Contain("PractitionerRole");
    }

    [Fact]
    public async Task ReadPractitionerRole_handles_upstream_unreachable_as_502()
    {
        _providerServiceHandler.Respond(_ =>
            throw new HttpRequestException("connection refused"));

        var result = await _controller.ReadPractitionerRole(
            "1234567890-1-20240101-network-a", default);
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        status.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task SearchPractitionerRoles_forwards_query_string_to_provider_service()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(
            "?practitioner=Practitioner/1234567890&organization=Organization/net-a&specialty=cardiology&_count=25");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchPractitionerRoles(default);

        var req = _providerServiceHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/PractitionerRole");
        req.RequestUri.Query.Should().Contain("practitioner=Practitioner/1234567890");
        req.RequestUri.Query.Should().Contain("organization=Organization/net-a");
        req.RequestUri.Query.Should().Contain("specialty=cardiology");
        req.RequestUri.Query.Should().Contain("_count=25");
    }

    [Fact]
    public async Task PractitionerRole_path_does_not_call_NPPES_or_verification()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?practitioner=Practitioner/1234567890");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        await _controller.SearchPractitionerRoles(default);

        _nppesHandler.Calls.Should().BeEmpty(
            "the NPPES path is dead for PractitionerRole now");
        _verificationHandler.Calls.Should().BeEmpty(
            "verification metadata stays on the linked Practitioner — PractitionerRole proxy never enriches");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
        };

    /// <summary>
    /// Test double for HttpMessageHandler that records each request and
    /// dispatches via a caller-supplied delegate. Mirror of the helper
    /// in <see cref="ProviderDirectoryControllerProxyTests"/>; keeping a
    /// per-fixture copy avoids cross-file private-type coupling.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = new();
        private Func<HttpRequestMessage, HttpResponseMessage> _responder =
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
