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
/// Capability 5.9 — proxy-shape coverage for the rewritten Organization
/// endpoints in <see cref="ProviderDirectoryController"/>. Mirrors the 5.7
/// Practitioner and 5.8 PractitionerRole proxy tests: the controller no
/// longer talks to NPPES on the Organization path; it issues a single GET
/// to the typed <c>ProviderService</c> HttpClient and passes the response
/// through.
///
/// Verifies:
/// <list type="bullet">
///   <item>read forwards GET /fhir/Organization/{id} to provider-service;</item>
///   <item>search forwards the original FHIR query string;</item>
///   <item>4xx and 2xx responses pass through verbatim;</item>
///   <item>5xx responses are translated to a FHIR 502 OperationOutcome;</item>
///   <item>upstream connection failure → 502 OperationOutcome;</item>
///   <item>NPPES and verification HttpClients are NOT called on the
///         Organization path.</item>
/// </list>
/// </summary>
public class ProviderDirectoryControllerOrganizationProxyTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _providerServiceHandler = new();
    private readonly RecordingHandler _verificationHandler = new();
    private readonly RecordingHandler _nppesHandler = new();
    private readonly ProviderDirectoryController _controller;

    public ProviderDirectoryControllerOrganizationProxyTests()
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
    public async Task ReadOrganization_forwards_to_provider_service_with_correct_path()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Organization\",\"id\":\"1234567890\",\"type\":[{\"coding\":[{\"code\":\"prov\"}]}]}"));

        var result = await _controller.ReadOrganization("1234567890", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"Organization\"");

        _providerServiceHandler.Calls.Should().ContainSingle();
        var req = _providerServiceHandler.Calls[0];
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/Organization/1234567890");
    }

    [Fact]
    public async Task ReadOrganization_forwards_non_NPI_id_to_provider_service()
    {
        var orgId = "aaaa-bbbb-cccc-dddd";
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            $"{{\"resourceType\":\"Organization\",\"id\":\"{orgId}\",\"type\":[{{\"coding\":[{{\"code\":\"ins\"}}]}}]}}"));

        var result = await _controller.ReadOrganization(orgId, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);

        var req = _providerServiceHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be($"/fhir/Organization/{orgId}");
    }

    [Fact]
    public async Task ReadOrganization_passes_404_OperationOutcome_through()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.NotFound,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));

        var result = await _controller.ReadOrganization("does-not-exist", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task ReadOrganization_translates_upstream_5xx_to_502_OperationOutcome()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "internal upstream noise that must NOT leak"));

        var result = await _controller.ReadOrganization("1234567890", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue.Single().Diagnostics.Should().NotContain("internal upstream noise",
            "upstream 5xx body must not leak to consumers");
        outcome.Issue.Single().Diagnostics.Should().Contain("Organization",
            "label should identify the resource for operator triage");
    }

    [Fact]
    public async Task ReadOrganization_handles_upstream_unreachable_as_502()
    {
        _providerServiceHandler.Respond(_ =>
            throw new HttpRequestException("connection refused"));

        var result = await _controller.ReadOrganization("1234567890", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        status.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task SearchOrganizations_forwards_query_string_to_provider_service()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(
            "?npi=1234567890&name=Acme&city=Boston&state=MA&postal-code=02101&_count=25");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchOrganizations(default);

        var req = _providerServiceHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/Organization");
        req.RequestUri.Query.Should().Contain("npi=1234567890");
        req.RequestUri.Query.Should().Contain("name=Acme");
        req.RequestUri.Query.Should().Contain("city=Boston");
        req.RequestUri.Query.Should().Contain("state=MA");
        req.RequestUri.Query.Should().Contain("postal-code=02101");
        req.RequestUri.Query.Should().Contain("_count=25");
    }

    [Fact]
    public async Task SearchOrganizations_forwards_identifier_parameter_to_provider_service()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?identifier=ORG%3Amet-001");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":1,\"entry\":[]}"));

        await _controller.SearchOrganizations(default);

        var req = _providerServiceHandler.Calls.Single();
        req.RequestUri!.Query.Should().Contain("identifier=");
    }

    [Fact]
    public async Task Organization_path_does_not_call_NPPES_or_verification()
    {
        _providerServiceHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Organization\",\"id\":\"1234567890\"}"));

        await _controller.ReadOrganization("1234567890", default);

        _nppesHandler.Calls.Should().BeEmpty(
            "NPPES is no longer the source for Organization after 5.9");
        _verificationHandler.Calls.Should().BeEmpty(
            "verification metadata is on Practitioner, not Organization (Decision 5)");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
        };

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
