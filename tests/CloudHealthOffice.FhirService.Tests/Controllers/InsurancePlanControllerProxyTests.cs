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
/// Capability BP 5.8 — proxy-shape coverage for the new
/// <see cref="InsurancePlanController"/>. Mirrors the 5.7 / 5.8 / 5.9
/// Provider-domain proxy tests: the fhir-service controller talks to a
/// typed <c>BenefitPlanService</c> HttpClient and passes the response
/// through.
///
/// Verifies:
/// <list type="bullet">
///   <item>read forwards GET /fhir/InsurancePlan/{id} to benefit-plan-service;</item>
///   <item>search forwards the original FHIR query string;</item>
///   <item>4xx and 2xx responses pass through verbatim;</item>
///   <item>5xx responses are translated to a FHIR 502 OperationOutcome
///         that does NOT leak the upstream body;</item>
///   <item>upstream connection failure → 502 OperationOutcome;</item>
///   <item>label "InsurancePlan" appears in 502 diagnostics so operators
///         can triage by resource type;</item>
///   <item>the BenefitPlanService HttpClient is the only client called.</item>
/// </list>
/// </summary>
public class InsurancePlanControllerProxyTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _benefitPlanHandler = new();
    private readonly InsurancePlanController _controller;

    public InsurancePlanControllerProxyTests()
    {
        _factory.Setup(f => f.CreateClient(InsurancePlanController.BenefitPlanServiceClientName))
            .Returns(() => new HttpClient(_benefitPlanHandler)
            {
                BaseAddress = new Uri("http://benefit-plan-service.test/")
            });

        _controller = new InsurancePlanController(
            _factory.Object,
            NullLogger<InsurancePlanController>.Instance);
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    [Fact]
    public async Task ReadInsurancePlan_forwards_to_benefit_plan_service_with_correct_path()
    {
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"InsurancePlan\",\"id\":\"AUR-GOLD-PPO-2026\",\"status\":\"active\"}"));

        var result = await _controller.ReadInsurancePlan("AUR-GOLD-PPO-2026", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"InsurancePlan\"");

        _benefitPlanHandler.Calls.Should().ContainSingle();
        var req = _benefitPlanHandler.Calls[0];
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/InsurancePlan/AUR-GOLD-PPO-2026");
    }

    [Fact]
    public async Task ReadInsurancePlan_url_encodes_the_id()
    {
        var planId = "PLAN id with spaces";
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"InsurancePlan\",\"id\":\"x\"}"));

        await _controller.ReadInsurancePlan(planId, default);

        var req = _benefitPlanHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().NotContain(" ");
        req.RequestUri.AbsolutePath.Should().Contain("PLAN%20id%20with%20spaces");
    }

    [Fact]
    public async Task ReadInsurancePlan_passes_404_OperationOutcome_through_verbatim()
    {
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.NotFound,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));

        var result = await _controller.ReadInsurancePlan("does-not-exist", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task ReadInsurancePlan_translates_upstream_5xx_to_502_OperationOutcome_without_leaking_body()
    {
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.InternalServerError,
            "internal upstream noise that must NOT leak"));

        var result = await _controller.ReadInsurancePlan("AUR-GOLD-PPO-2026", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue.Single().Diagnostics.Should().NotContain("internal upstream noise",
            "upstream 5xx body must not leak to consumers");
        outcome.Issue.Single().Diagnostics.Should().Contain("InsurancePlan",
            "label should identify the resource for operator triage");
    }

    [Fact]
    public async Task ReadInsurancePlan_handles_upstream_unreachable_as_502()
    {
        _benefitPlanHandler.Respond(_ =>
            throw new HttpRequestException("connection refused"));

        var result = await _controller.ReadInsurancePlan("AUR-GOLD-PPO-2026", default);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        status.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task ReadInsurancePlan_propagates_caller_cancellation()
    {
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"InsurancePlan\"}"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _controller.ReadInsurancePlan("PLAN", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchInsurancePlans_forwards_query_string_to_benefit_plan_service()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(
            "?identifier=AUR-GOLD-PPO-2026&name=Aurelian&status=active&_count=25");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchInsurancePlans(default);

        var req = _benefitPlanHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/InsurancePlan");
        req.RequestUri.Query.Should().Contain("identifier=AUR-GOLD-PPO-2026");
        req.RequestUri.Query.Should().Contain("name=Aurelian");
        req.RequestUri.Query.Should().Contain("status=active");
        req.RequestUri.Query.Should().Contain("_count=25");
    }

    [Fact]
    public async Task SearchInsurancePlans_with_empty_query_string_still_hits_search_path()
    {
        _benefitPlanHandler.Respond(_ => Json(HttpStatusCode.OK,
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0,\"entry\":[]}"));

        await _controller.SearchInsurancePlans(default);

        var req = _benefitPlanHandler.Calls.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/fhir/InsurancePlan");
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
