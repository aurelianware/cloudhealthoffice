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
/// Capability 5.7 — proxy-shape coverage for the rewritten Practitioner
/// endpoints in <see cref="ProviderDirectoryController"/>. The
/// controller no longer talks to NPPES or
/// <c>ProviderVerificationService</c> on the Practitioner path; it issues
/// a single GET to the typed <c>ProviderService</c> HttpClient and
/// passes the response through. These tests assert:
///
/// <list type="bullet">
///   <item>read forwards GET /fhir/Practitioner/{id} to provider-service;</item>
///   <item>search forwards the original FHIR query string;</item>
///   <item>4xx and 2xx responses pass through (status + body + content-type);</item>
///   <item>5xx responses are translated to a FHIR 502 OperationOutcome
///         (no upstream-body leak);</item>
///   <item>upstream connection failure → 502 OperationOutcome;</item>
///   <item>NPPES and verification HttpClients are NOT touched on the
///         Practitioner path.</item>
/// </list>
/// </summary>
public class ProviderDirectoryControllerProxyTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _providerServiceHandler = new();
    private readonly RecordingHandler _verificationHandler = new();
    private readonly RecordingHandler _nppesHandler = new();
    private readonly ProviderDirectoryController _controller;

    public ProviderDirectoryControllerProxyTests()
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
    public async Task ReadPractitioner_forwards_to_provider_service_with_NPI_path()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Practitioner\",\"id\":\"1234567890\"}"));

        var result = await _controller.ReadPractitioner("1234567890", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"Practitioner\"");

        _providerServiceHandler.Calls.Should().ContainSingle();
        var req = _providerServiceHandler.Calls[0];
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/Practitioner/1234567890");
    }

    [Fact]
    public async Task ReadPractitioner_passes_404_OperationOutcome_through()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.NotFound,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));

        var result = await _controller.ReadPractitioner("9999999999", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
        content.Content.Should().Contain("not-found");
    }

    [Fact]
    public async Task ReadPractitioner_translates_upstream_5xx_to_502_OperationOutcome()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "internal upstream noise that must NOT leak"));

        var result = await _controller.ReadPractitioner("1234567890", default);
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        // FhirControllerBase.FhirBadGateway(...) returns the typed FHIR
        // OperationOutcome model. Asserting on the type is more robust
        // than serialising and grepping for "resourceType" — the FHIR
        // formatter pipeline emits that, not raw JSON.
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue.Single().Diagnostics.Should().NotContain("internal upstream noise");
    }

    [Fact]
    public async Task ReadPractitioner_handles_upstream_unreachable_as_502()
    {
        _providerServiceHandler.Respond(_ =>
            throw new HttpRequestException("connection refused"));

        var result = await _controller.ReadPractitioner("1234567890", default);
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        status.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task SearchPractitioners_forwards_query_string_to_provider_service()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?family=Smith&city=Boston&state=MA");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchPractitioners(default);

        var req = _providerServiceHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/Practitioner");
        req.RequestUri.Query.Should().Contain("family=Smith");
        req.RequestUri.Query.Should().Contain("city=Boston");
        req.RequestUri.Query.Should().Contain("state=MA");
    }

    [Fact]
    public async Task Practitioner_path_does_not_call_NPPES_or_verification()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Practitioner\",\"id\":\"1234567890\"}"));

        await _controller.ReadPractitioner("1234567890", default);

        _nppesHandler.Calls.Should().BeEmpty(
            "the NPPES path is dead for Practitioner now");
        _verificationHandler.Calls.Should().BeEmpty(
            "verification enrichment is folded into the provider-service projection");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
        };

    /// <summary>
    /// Test double for HttpMessageHandler that records each request and
    /// dispatches via a caller-supplied delegate.
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
