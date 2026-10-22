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
/// CMS-0057-F PA data over FHIR (gap matrix G-31, and the PA half of G-33 and
/// G-35). Da Vinci PDex represents a prior authorization as an
/// <c>ExplanationOfBenefit</c> with <c>use = preauthorization</c>, so one
/// resource type has two owners: claims-service projects claims,
/// authorization-service projects prior authorizations.
///
/// What these pin is the routing between them and the confinement that applies
/// to both — a patient-bound token (Patient Access) and a provider token just
/// authorized for one member (Provider Access) reach prior-auth data through
/// the same path and under the same checks as claims.
/// </summary>
public class PriorAuthorizationEobRoutingTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly RecordingHandler _claims = new();
    private readonly RecordingHandler _authorizations = new();
    private readonly ExplanationOfBenefitController _controller;

    public PriorAuthorizationEobRoutingTests()
    {
        _factory.Setup(f => f.CreateClient(ExplanationOfBenefitController.ClaimsServiceClientName))
            .Returns(() => new HttpClient(_claims) { BaseAddress = new Uri("http://claims-service.test/") });
        _factory.Setup(f => f.CreateClient("AuthorizationService"))
            .Returns(() => new HttpClient(_authorizations) { BaseAddress = new Uri("http://authorization-service.test/") });

        _controller = new ExplanationOfBenefitController(
            _factory.Object, NullLogger<ExplanationOfBenefitController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // ── read routing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AReadOfAPrefixedId_GoesToAuthorizationService()
    {
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, PriorAuthJson("M1")));

        var result = await _controller.ReadEob("pa-PA-1", default);

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
        _claims.Calls.Should().BeEmpty("a prior-auth id is not claims-service's to answer");
        _authorizations.Calls.Single().RequestUri!.AbsolutePath
            .Should().Be("/fhir/ExplanationOfBenefit/prior-authorization/pa-PA-1");
    }

    [Fact]
    public async Task AReadOfAClaimVersionId_StillGoesToClaimsService()
    {
        _claims.Respond(_ => Fhir(HttpStatusCode.OK,
            "{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"CHAIN-1\"}"));

        await _controller.ReadEob("CHAIN-1", default);

        _authorizations.Calls.Should().BeEmpty();
        _claims.Calls.Single().RequestUri!.AbsolutePath.Should().Be("/fhir/ExplanationOfBenefit/CHAIN-1");
    }

    [Theory]
    [InlineData("pa-PA-1", true)]
    [InlineData("  pa-PA-1  ", true)]
    [InlineData("CHAIN-1", false)]
    // Case matters: the prefix authorization-service writes is lower-case, and a
    // claim id that merely starts with "PA" is not a prior authorization.
    [InlineData("PA-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheIdPrefixDecidesTheOwner(string? id, bool expected)
    {
        ExplanationOfBenefitController.IsPriorAuthorizationId(id).Should().Be(expected);
    }

    [Fact]
    public async Task APriorAuthReadIsUrlEncodedLikeAClaimRead()
    {
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, PriorAuthJson("M1")));

        await _controller.ReadEob("pa-id with/slash", default);

        var path = _authorizations.Calls.Single().RequestUri!.AbsolutePath;
        path.Should().Contain("pa-id%20with%2Fslash");
        path.Should().NotContain(" ");
    }

    // ── search routing ───────────────────────────────────────────────────────

    [Fact]
    public async Task ASearchWithUsePreauthorization_GoesToAuthorizationService()
    {
        Query("patient=M1&use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle(PriorAuthJson("M1"))));

        var result = await _controller.SearchEobs();

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
        _claims.Calls.Should().BeEmpty();
        var uri = _authorizations.Calls.Single().RequestUri!;
        uri.AbsolutePath.Should().Be("/fhir/ExplanationOfBenefit/prior-authorization");
        uri.Query.Should().Contain("patient=M1");
    }

    [Fact]
    public async Task ASearchWithoutUse_IsStillTheClaimsSearch()
    {
        // The two result sets are deliberately not merged; `use` is how a
        // consumer asks for prior authorizations, and the CapabilityStatement
        // says so.
        Query("patient=M1");
        _claims.Respond(_ => Fhir(HttpStatusCode.OK, Bundle()));

        await _controller.SearchEobs();

        _authorizations.Calls.Should().BeEmpty();
        _claims.Calls.Single().RequestUri!.AbsolutePath.Should().Be("/fhir/ExplanationOfBenefit");
    }

    [Fact]
    public async Task ASearchForAnotherUseValue_IsNotAPriorAuthSearch()
    {
        // `use=claim` is a claims question, and claims-service decides what it
        // supports — this proxy does not reinterpret it.
        Query("patient=M1&use=claim");
        _claims.Respond(_ => Fhir(HttpStatusCode.OK, Bundle()));

        await _controller.SearchEobs();

        _authorizations.Calls.Should().BeEmpty();
        _claims.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task TheUseValueIsCaseInsensitive()
    {
        Query("patient=M1&use=PreAuthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle()));

        await _controller.SearchEobs();

        _authorizations.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task APriorAuthSearchWithoutAPatient_IsRefusedBeforeTheHop()
    {
        Query("use=preauthorization");

        var result = await _controller.SearchEobs();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _authorizations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheSupportedParametersAreForwarded()
    {
        // Forwarding everything would imply support this route does not have.
        Query("patient=M1&use=preauthorization&status=Pended&_count=10&_page=2&created=2026-01-01&_include=*");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle()));

        await _controller.SearchEobs();

        var query = _authorizations.Calls.Single().RequestUri!.Query;
        query.Should().Contain("patient=M1");
        query.Should().Contain("status=Pended");
        query.Should().Contain("_count=10");
        query.Should().Contain("_page=2");
        query.Should().NotContain("created");
        query.Should().NotContain("_include");
        // `use` selected the upstream and means nothing downstream.
        query.Should().NotContain("use=");
    }

    // ── confinement ──────────────────────────────────────────────────────────

    [Fact]
    public async Task APatientBoundTokenPinsThePriorAuthSearchToItsOwnMember()
    {
        // Patient Access: the caller does not need to know its own member id,
        // and a patient parameter naming someone else cannot widen the search.
        Bind("M1");
        Query("patient=M2&use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle(PriorAuthJson("M1"))));

        await _controller.SearchEobs();

        _authorizations.Calls.Single().RequestUri!.Query.Should().Contain("patient=M1");
    }

    [Fact]
    public async Task AReadOfAnotherMembersPriorAuthorizationIs404()
    {
        // Not 403: a confined caller must not learn that another member's
        // authorization exists.
        Bind("M1");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, PriorAuthJson("M2")));

        var result = await _controller.ReadEob("pa-PA-1", default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task AConfinedSearchWhoseResultsLeaveTheMemberIsWithheldEntirely()
    {
        // The upstream ignoring its member filter is a bug, not something to
        // silently trim: nothing of the answer is returned.
        Bind("M1");
        Query("use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle(PriorAuthJson("M1"), PriorAuthJson("M2"))));

        var result = await _controller.SearchEobs();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task AProviderAuthorizedForOneMemberIsConfinedToThatMember()
    {
        // Provider Access reaches prior-auth data through the same path: the
        // filter records the one member it authorized, and this controller pins
        // the search to it exactly as it does a patient-bound token.
        _controller.ControllerContext.HttpContext.Items[
            global::FhirService.Services.ProviderAccess.ProviderAccessAuthorizationFilter
                .AuthorizedMemberItemKey] = "M9";
        Query("patient=M1&use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.OK, Bundle(PriorAuthJson("M9"))));

        var result = await _controller.SearchEobs();

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
        _authorizations.Calls.Single().RequestUri!.Query.Should().Contain("patient=M9");
    }

    // ── upstream failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnUpstream5xxBecomesA502ThatDoesNotLeakTheBody()
    {
        Query("patient=M1&use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.InternalServerError,
            "{\"internal\":\"connection string nobody should see\"}"));

        var result = await _controller.SearchEobs();

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(502);
        var outcome = status.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Diagnostics.Should().Contain("ExplanationOfBenefit");
        outcome.Issue.Single().Diagnostics.Should().NotContain("connection string");
    }

    [Fact]
    public async Task AnUpstream4xxPassesThroughVerbatim()
    {
        Query("patient=M1&use=preauthorization");
        _authorizations.Respond(_ => Fhir(HttpStatusCode.BadRequest,
            "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"code\":\"invalid\"}]}"));

        var content = (await _controller.SearchEobs()).Should().BeOfType<ContentResult>().Subject;

        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("\"code\":\"invalid\"");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void Query(string queryString)
        => _controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?" + queryString);

    private void Bind(string memberId)
        => _controller.ControllerContext.HttpContext.Items["SmartPatientId"] = memberId;

    private static string PriorAuthJson(string memberId)
        => $"{{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"pa-PA-1\",\"use\":\"preauthorization\"," +
           $"\"patient\":{{\"reference\":\"Patient/{memberId}\"}}}}";

    private static string Bundle(params string[] resources)
        => "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":" + resources.Length +
           ",\"entry\":[" + string.Join(",", resources.Select(r => "{\"resource\":" + r + "}")) + "]}";

    private static HttpResponseMessage Fhir(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/fhir+json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = new();
        private Func<HttpRequestMessage, HttpResponseMessage> _responder =
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
