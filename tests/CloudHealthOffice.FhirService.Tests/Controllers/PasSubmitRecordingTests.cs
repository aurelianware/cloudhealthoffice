using System.Net;
using System.Text.Json;
using CloudHealthOffice.FhirService.Tests.TestSupport;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Pas;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// D-01 / D-03 / D-08 at the controller: $submit refuses an incomplete request
/// before deciding anything, never answers with a preAuthRef unless the record
/// was stored, answers with what was stored, and carries urgency and the
/// receipt time into the record.
/// </summary>
public class PasSubmitRecordingTests
{
    private readonly Mock<IPasAutoAdjudicator> _adjudicator = new();
    private readonly FakePasAuthorizationRecorder _recorder = new();

    private PasController Controller(PasDecisionResult? decision = null)
    {
        _adjudicator
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision ?? new PasDecisionResult
            {
                HasDecision = true, Decision = "approved", RuleName = "AutoApproveList",
            });

        var compliance = new Mock<ICms0057ComplianceChecker>();
        compliance.Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns(new ComplianceResult(true, [], [],
                new ComplianceSummary("Claim", 10, 10, [], [], [], new TimelineCompliance(true))));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StatusHandler(HttpStatusCode.NotFound))
            {
                BaseAddress = new Uri("http://provider-verification-service.test/"),
            });

        var controller = new PasController(
            _adjudicator.Object,
            new PasResponseBuilder(),
            compliance.Object,
            new Mock<IPriorAuthorizationInquiryService>().Object,
            Options.Create(new PasAutoAdjudicationConfig { Enabled = true, MaxResponseMs = 12000 }),
            factory.Object,
            _recorder,
            NullLogger<PasController>.Instance);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private void AdjudicatorNeverRan() =>
        _adjudicator.Verify(a => a.TryDecideAsync(
            It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

    // ── Incomplete requests: 422, nothing decided, nothing stored ────────

    [Fact]
    public async Task MissingPatient_Returns422_NothingDecidedOrStored()
    {
        var result = await Controller().ClaimSubmit(PasBundles.Complete().Without<Patient>());

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(422);
        refused.Value.Should().BeOfType<OperationOutcome>()
            .Which.Issue.Should().Contain(i => i.Expression.Contains("Claim.patient")
                                               && i.Code == OperationOutcome.IssueType.Required);
        AdjudicatorNeverRan();
        _recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingNpi_Returns422_NothingDecidedOrStored()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Practitioner>().Single().Identifier.Clear();

        var result = await Controller().ClaimSubmit(bundle);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        AdjudicatorNeverRan();
        _recorder.Calls.Should().BeEmpty();
    }

    // ── Persistence failure: no ClaimResponse, no preAuthRef ─────────────

    [Theory]
    [InlineData(PasRecordFailure.Unavailable, 503)]
    [InlineData(PasRecordFailure.Rejected, 502)]
    public async Task RecordNotStored_FailsWithOperationOutcome_AndNoPreAuthRef(PasRecordFailure failure, int status)
    {
        _recorder.Failure = failure;

        var result = await Controller().ClaimSubmit(PasBundles.Complete());

        var failed = result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(status);
        var outcome = failed.Value.Should().BeOfType<OperationOutcome>().Subject;
        JsonSerializer.Serialize(outcome).Should().NotContain("PAS-",
            "no tracking number may be handed out for a record that was not stored");
        _recorder.Calls.Should().ContainSingle();
    }

    // ── What is stored, and the answer follows it ───────────────────────

    [Fact]
    public async Task Approval_StoresDecisionAndRequestedPeriod_AndAnswersWithTheStoredNumber()
    {
        var result = await Controller().ClaimSubmit(PasBundles.Complete());

        var claimResponse = ((Bundle)result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        var stored = _recorder.Calls.Should().ContainSingle().Subject;
        stored.TenantId.Should().Be("tenant-a");
        stored.Request.AuthorizationNumber.Should().Be(claimResponse.PreAuthRef);
        stored.Request.Decision!.Outcome.Should().Be("Approved");
        stored.Request.Decision.Rule.Should().Be("AutoApproveList");
        stored.Request.Decision.ApprovedServiceDateFrom.Should().Be(new DateTime(2026, 11, 2));
        stored.Request.Decision.ApprovedServiceDateTo.Should().Be(new DateTime(2026, 11, 4));
        claimResponse.PreAuthPeriod.Start.Should().Be("2026-11-02");
        claimResponse.PreAuthPeriod.End.Should().Be("2026-11-04");
    }

    [Fact]
    public async Task Denial_StoresCodeAndReason()
    {
        await Controller(new PasDecisionResult
        {
            HasDecision = true, Decision = "denied", DenialReasonCode = "NOT_COVERED",
            DenialReason = "Service 27447 is not a covered benefit", RuleName = "AutoDenyList",
        }).ClaimSubmit(PasBundles.Complete());

        var decision = _recorder.Last!.Decision!;
        decision.Outcome.Should().Be("Denied");
        decision.DenialReasonCode.Should().Be("NOT_COVERED");
        decision.DenialReason.Should().Be("Service 27447 is not a covered benefit");
        decision.ApprovedServiceDateFrom.Should().BeNull();
    }

    [Fact]
    public async Task StoredAsDeniedByPlanExclusion_AnswersDenied_NotApproved()
    {
        _recorder.StoredOverride = new PasStoredAuthorization
        {
            Status = PriorAuthorizationStatus.Denied,
            DenialReasonCode = "EXCL-NONCOV",
            DenialReason = "Requested drug/service is excluded by the member's benefit plan.",
        };

        var result = await Controller().ClaimSubmit(PasBundles.Complete());

        var claimResponse = ((Bundle)result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.Disposition.Should().Be("denied");
        claimResponse.Error.Should().Contain(e => e.Code.Coding.Any(c => c.Code == "EXCL-NONCOV"));
    }

    // ── D-08 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("stat", "U")]
    [InlineData("urgent", "U")]
    [InlineData("normal", "E")]
    [InlineData(null, null)]
    public async Task Priority_IsStoredAsLevelOfService(string? priority, string? levelOfService)
    {
        await Controller().ClaimSubmit(PasBundles.Complete(priority: priority));

        _recorder.Last!.LevelOfService.Should().Be(levelOfService);
    }

    [Fact]
    public async Task ReceiptIsStampedBeforeAdjudication()
    {
        DateTime? adjudicatedAt = null;
        var controller = Controller();
        _adjudicator
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                adjudicatedAt = DateTime.UtcNow;
                await Task.Delay(15);
                return new PasDecisionResult { HasDecision = false, RuleName = "NoOp" };
            });

        await controller.ClaimSubmit(PasBundles.Complete());

        _recorder.Last!.ReceivedAt.Should().BeOnOrBefore(adjudicatedAt!.Value);
        _recorder.Last.Decision!.DecidedAt.Should().BeOnOrAfter(_recorder.Last.ReceivedAt);
        _recorder.Last.Decision.Outcome.Should().Be("Pended");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}

/// <summary>
/// The HTTP recorder: fhir-service's own service token (never the caller's),
/// and every failure mode reported as not recorded.
/// </summary>
public class HttpPasAuthorizationRecorderTests
{
    private const string Tenant = "tenant-a";

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return respond(request);
        }
    }

    private static (HttpPasAuthorizationRecorder Recorder, Handler Handler) Recorder(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool withServiceToken = true)
    {
        var handler = new Handler(respond);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpPasAuthorizationRecorder.HttpClientName))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://authorization-service.cloudhealthoffice/") });

        var services = new ServiceCollection();
        if (withServiceToken)
            services.AddSingleton<IChoServiceTokenSource>(
                new LocalKeyChoServiceTokenSource(ChoDevelopmentAuth.ServiceTokenIssuer(), "fhir-service"));

        return (new HttpPasAuthorizationRecorder(factory.Object, services.BuildServiceProvider(),
            NullLogger<HttpPasAuthorizationRecorder>.Instance), handler);
    }

    private static PasAuthorizationRecordRequest Request() => new()
    {
        AuthorizationNumber = "PAS-20261011-ABCDEF01",
        Decision = new PasAutomatedDecision { Outcome = "Approved" },
    };

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, object body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task Stored_ReturnsTheStoredRecord_SentWithFhirServicesOwnTokenForTheTenant()
    {
        var (recorder, handler) = Recorder(_ => Json(HttpStatusCode.Created, new
        {
            tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved",
        }));

        var outcome = await recorder.RecordAsync(Request(), Tenant);

        outcome.Recorded.Should().BeTrue();
        outcome.Stored!.Status.Should().Be(PriorAuthorizationStatus.Approved);
        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/" + HttpPasAuthorizationRecorder.Path);
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(handler.Request.Headers.Authorization.Parameter);
        token.Subject.Should().Be("fhir-service");
        token.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == Tenant);
        token.Claims.Should().Contain(c => c.Type == "roles" && c.Value == ChoServiceRole.Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, PasRecordFailure.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, PasRecordFailure.Rejected)]
    [InlineData(HttpStatusCode.Conflict, PasRecordFailure.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, PasRecordFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PasRecordFailure.Unavailable)]
    public async Task ErrorStatus_IsNotRecorded(HttpStatusCode status, PasRecordFailure failure)
    {
        var (recorder, _) = Recorder(_ => Task.FromResult(new HttpResponseMessage(status)));

        var outcome = await recorder.RecordAsync(Request(), Tenant);

        outcome.Recorded.Should().BeFalse();
        outcome.Failure.Should().Be(failure);
    }

    [Fact]
    public async Task TransportFailure_IsUnavailable()
    {
        var (recorder, _) = Recorder(_ => throw new HttpRequestException("connection refused"));

        (await recorder.RecordAsync(Request(), Tenant)).Failure.Should().Be(PasRecordFailure.Unavailable);
    }

    [Fact]
    public async Task NoServiceToken_IsUnavailable_AndNothingIsSent()
    {
        var (recorder, handler) = Recorder(_ => Json(HttpStatusCode.Created, new { }), withServiceToken: false);

        (await recorder.RecordAsync(Request(), Tenant)).Failure.Should().Be(PasRecordFailure.Unavailable);
        handler.Request.Should().BeNull();
    }

    [Theory]
    [InlineData("tenant-b", "PAS-20261011-ABCDEF01")]
    [InlineData(Tenant, "PAS-SOMETHING-ELSE")]
    public async Task ADifferentRecordBack_IsRejected(string tenant, string number)
    {
        var (recorder, _) = Recorder(_ => Json(HttpStatusCode.Created, new
        {
            tenantId = tenant, authorizationNumber = number, status = "Approved",
        }));

        (await recorder.RecordAsync(Request(), Tenant)).Failure.Should().Be(PasRecordFailure.Rejected);
    }
}
