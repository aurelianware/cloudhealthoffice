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
/// Capability 5.11 — proxy-shape coverage for the migrated
/// <see cref="ExplanationOfBenefitController"/>. Mirrors the BP 5.8
/// <see cref="InsurancePlanController"/> proxy tests: the fhir-service
/// controller talks to a typed <c>ClaimsService</c> HttpClient and passes
/// the claims-service response through.
///
/// Verifies:
/// <list type="bullet">
///   <item>read forwards GET /fhir/ExplanationOfBenefit/{id} to claims-service;</item>
///   <item>id is URL-encoded so spaces / slashes don't bypass the upstream route;</item>
///   <item>search forwards the FHIR query string with auto-injected SMART patient;</item>
///   <item>search rejects requests missing both <c>patient</c>, <c>_id</c>, and SMART binding;</item>
///   <item>4xx and 2xx responses pass through verbatim;</item>
///   <item>5xx responses are translated to a FHIR 502 OperationOutcome
///         that does NOT leak the upstream body;</item>
///   <item>upstream connection failure → 502 OperationOutcome;</item>
///   <item>label "ExplanationOfBenefit" appears in 502 diagnostics so operators
///         can triage by resource type.</item>
/// </list>
/// </summary>
public class ExplanationOfBenefitControllerProxyTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _claimsHandler = new();
    private readonly ExplanationOfBenefitController _controller;

    public ExplanationOfBenefitControllerProxyTests()
    {
        _factory.Setup(f => f.CreateClient(ExplanationOfBenefitController.ClaimsServiceClientName))
            .Returns(() => new HttpClient(_claimsHandler)
            {
                BaseAddress = new Uri("http://claims-service.test/")
            });

        _controller = new ExplanationOfBenefitController(
            _factory.Object,
            NullLogger<ExplanationOfBenefitController>.Instance);
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadEob_forwards_to_claims_service_with_correct_path()
    {
        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"CHAIN-1\",\"status\":\"active\"}"));

        var result = await _controller.ReadEob("CHAIN-1", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"ExplanationOfBenefit\"");

        _claimsHandler.Calls.Should().ContainSingle();
        var req = _claimsHandler.Calls[0];
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/ExplanationOfBenefit/CHAIN-1");
    }

    [Fact]
    public async Task ReadEob_url_encodes_the_id()
    {
        var id = "id with/slash";
        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK, "{\"resourceType\":\"ExplanationOfBenefit\"}"));

        await _controller.ReadEob(id, default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().NotContain(" ");
        req.RequestUri.AbsolutePath.Should().NotContain("id with/slash",
            "raw spaces and slashes must be encoded so they don't bypass the upstream route binding");
        req.RequestUri.AbsolutePath.Should().Contain("id%20with%2Fslash");
    }

    [Fact]
    public async Task ReadEob_passes_404_OperationOutcome_through_verbatim()
    {
        _claimsHandler.Respond(_ => Json(HttpStatusCode.NotFound,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));

        var result = await _controller.ReadEob("missing", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task ReadEob_translates_upstream_5xx_to_502_OperationOutcome_without_leaking_body()
    {
        _claimsHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "internal upstream noise that must NOT leak"));

        var result = await _controller.ReadEob("CHAIN-1", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue.Single().Diagnostics.Should().NotContain("internal upstream noise",
            "upstream 5xx body must not leak to consumers");
        outcome.Issue.Single().Diagnostics.Should().Contain("ExplanationOfBenefit",
            "label should identify the resource for operator triage");
    }

    [Fact]
    public async Task ReadEob_handles_upstream_unreachable_as_502()
    {
        _claimsHandler.Respond(_ =>
            throw new HttpRequestException("connection refused"));

        var result = await _controller.ReadEob("CHAIN-1", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        status.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task ReadEob_propagates_caller_cancellation()
    {
        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"ExplanationOfBenefit\"}"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _controller.ReadEob("CHAIN-1", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchEobs_with_explicit_patient_forwards_query_string()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?patient=MEM-7&_count=25");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchEobs(default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/ExplanationOfBenefit");
        req.RequestUri.Query.Should().Contain("patient=MEM-7");
        req.RequestUri.Query.Should().Contain("_count=25");
    }

    [Fact]
    public async Task SearchEobs_auto_injects_SMART_patient_when_caller_omits_param()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["SmartPatientId"] = "SMART-PAT-9";
        ctx.Request.QueryString = new QueryString("?_count=10");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchEobs(default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.Query.Should().Contain("patient=SMART-PAT-9");
    }

    [Fact]
    public async Task SearchEobs_explicit_patient_overrides_SMART_binding_only_when_supplied()
    {
        // Middleware already validates that an explicit patient param
        // matches the SMART-bound id; this test asserts the proxy's
        // behavior given a request that's already past that gate (i.e.
        // the explicit value flows through unchanged).
        var ctx = new DefaultHttpContext();
        ctx.Items["SmartPatientId"] = "MEM-1";
        ctx.Request.QueryString = new QueryString("?patient=MEM-1&_count=5");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchEobs(default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.Query.Should().Contain("patient=MEM-1");
        // patient should appear exactly once — no double append from the
        // auto-inject path.
        req.RequestUri.Query.Split('&').Where(s => s.Contains("patient="))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task SearchEobs_strips_FHIR_typed_reference_from_explicit_patient_param()
    {
        // FHIR search params accept either bare ids or typed references
        // (`patient=Patient/MEM-7`). Without stripping, the upstream
        // receives `Patient/MEM-7` and the repository read for memberId
        // silently misses.
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?patient=Patient/MEM-7");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchEobs(default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.Query.Should().Contain("patient=MEM-7");
        req.RequestUri.Query.Should().NotContain("Patient%2FMEM-7",
            "the Patient/ prefix must be stripped before forwarding");
    }

    [Fact]
    public async Task SearchEobs_with_id_param_forwards_without_requiring_patient()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?_id=CHAIN-2");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchEobs(default);

        var req = _claimsHandler.Calls.Single();
        req.RequestUri!.Query.Should().Contain("_id=CHAIN-2");
        req.RequestUri.Query.Should().NotContain("patient=");
    }

    [Fact]
    public async Task SearchEobs_returns_400_OperationOutcome_when_no_patient_id_or_SMART()
    {
        // No explicit patient, no _id, no SmartPatientId — short-circuit.
        _claimsHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\"}"));

        var result = await _controller.SearchEobs(default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(400);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Invalid);

        _claimsHandler.Calls.Should().BeEmpty(
            "request must short-circuit — no upstream call when patient context is missing");
    }

    [Fact]
    public async Task SearchEobs_passes_4xx_OperationOutcome_through_verbatim()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?patient=MEM-7");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.BadRequest,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"invalid\"}]}"));

        var result = await _controller.SearchEobs(default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task SearchEobs_translates_upstream_5xx_to_502_with_resource_label()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?patient=MEM-7");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _claimsHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "leaky upstream message"));

        var result = await _controller.SearchEobs(default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Diagnostics.Should().Contain("ExplanationOfBenefit");
        outcome.Issue.Single().Diagnostics.Should().NotContain("leaky upstream message");
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
