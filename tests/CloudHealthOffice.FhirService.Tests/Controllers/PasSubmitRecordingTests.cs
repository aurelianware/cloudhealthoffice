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

    private PasController Controller(
        PasDecisionResult? decision = null, Microsoft.Extensions.Logging.ILogger<PasController>? logger = null)
    {
        _adjudicator
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            logger ?? NullLogger<PasController>.Instance);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private void AdjudicatorNeverRan() =>
        _adjudicator.Verify(a => a.TryDecideAsync(
            It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
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

    // ── SF1: the answer is built from the STORED record ─────────────────

    private static ClaimResponse ClaimResponseOf(IActionResult result)
        => ((Bundle)result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;

    [Fact]
    public async Task StoredApproval_AnswersWithTheStoredApprovedPeriod()
    {
        _recorder.StoredOverride = new PasStoredAuthorization
        {
            Status = PriorAuthorizationStatus.Approved,
            ApprovedServiceDateFrom = new DateTime(2026, 12, 1),
            ApprovedServiceDateTo = new DateTime(2026, 12, 31),
        };

        var claimResponse = ClaimResponseOf(await Controller().ClaimSubmit(PasBundles.Complete()));

        claimResponse.Disposition.Should().Be("approved");
        claimResponse.PreAuthRef.Should().Be(_recorder.Last!.AuthorizationNumber);
        claimResponse.PreAuthPeriod.Start.Should().Be("2026-12-01", "the period is the record's, not the decision's");
        claimResponse.PreAuthPeriod.End.Should().Be("2026-12-31");
    }

    [Fact]
    public async Task StoredPended_AnswersPended_EvenIfTheDecisionApproved()
    {
        _recorder.StoredOverride = new PasStoredAuthorization { Status = PriorAuthorizationStatus.Pended };

        var claimResponse = ClaimResponseOf(await Controller().ClaimSubmit(PasBundles.Complete()));

        claimResponse.Disposition.Should().Be("pended");
        claimResponse.PreAuthRef.Should().Be(_recorder.Last!.AuthorizationNumber);
        claimResponse.PreAuthPeriod.Should().BeNull();
    }

    [Fact]
    public async Task StoredDenied_AnswersWithTheStoredReason()
    {
        _recorder.StoredOverride = new PasStoredAuthorization
        {
            Status = PriorAuthorizationStatus.Denied, DenialReasonCode = "PEMS-001", DenialReason = "Not enrolled.",
        };

        var claimResponse = ClaimResponseOf(await Controller(new PasDecisionResult
        {
            HasDecision = true, Decision = "denied", DenialReasonCode = "PEMS-001",
            DenialReason = "Not enrolled.", RuleName = "EnrollmentGate",
        }).ClaimSubmit(PasBundles.Complete()));

        claimResponse.Disposition.Should().Be("denied");
        claimResponse.Error.Should().ContainSingle().Which.Code.Coding.Single().Code.Should().Be("PEMS-001");
    }

    [Fact]
    public async Task RetryAfterReviewerModification_ReturnsCommittedPartialLineDecision()
    {
        _recorder.StoredOverride = new PasStoredAuthorization
        {
            Status = PriorAuthorizationStatus.Modified, ReviewDecision = "A2",
            RequestedServices = new()
            {
                new() { LineNumber = 9, ProcedureCode = "72148", RequestedUnits = 4, ApprovedUnits = 2,
                    ServiceStatus = "A2", DenialReasonCode = "LIMIT", DenialReason = "Two units meet criteria." },
            },
        };
        var response = ClaimResponseOf(await Controller().ClaimSubmit(PasBundles.Complete()));
        response.Outcome.Should().Be(ClaimProcessingCodes.Partial);
        response.Item.Should().ContainSingle().Which.ItemSequence.Should().Be(9);
        response.Item[0].Adjudication.Single(a => a.Extension.Count > 0).Value.Should().Be(2);
    }

    [Theory]
    [InlineData(PriorAuthorizationStatus.Expired, "expired")]
    [InlineData(PriorAuthorizationStatus.Cancelled, "cancelled")]
    public async Task RetryAfterLifecycleClosure_ReturnsCommittedState(PriorAuthorizationStatus status, string disposition)
    {
        _recorder.StoredOverride = new PasStoredAuthorization { Status = status };
        var response = ClaimResponseOf(await Controller().ClaimSubmit(PasBundles.Complete()));
        response.Disposition.Should().Be(disposition);
        response.PreAuthRef.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(PriorAuthorizationStatus.Unknown)]
    [InlineData(PriorAuthorizationStatus.Submitted)]
    [InlineData(PriorAuthorizationStatus.InReview)]
    public async Task StoredInAStatusSubmitCannotAnswer_Returns502_WithNoPreAuthRef(PriorAuthorizationStatus status)
    {
        _recorder.StoredOverride = new PasStoredAuthorization { Status = status };

        var result = await Controller().ClaimSubmit(PasBundles.Complete());

        var failed = result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(502);
        var outcome = failed.Value.Should().BeOfType<OperationOutcome>().Subject;
        JsonSerializer.Serialize(outcome).Should().NotContain(_recorder.Last!.AuthorizationNumber);
    }

    // ── B1: the record carries the basis the decision used ───────────────

    [Fact]
    public async Task Decision_IsMadeOnTheMappedLineOfBusinessAndProviders_AndRecordedWithIt()
    {
        PasDecisionBasis? basis = null;
        var controller = Controller();
        _adjudicator
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Claim, Bundle, string, PasDecisionBasis, int, CancellationToken>((_, _, _, b, _, _) => basis = b)
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = false, RuleName = "NoRuleMatch", LineOfBusiness = "Commercial", StateCode = "TX",
            });

        // A FHIR date (servicedDate): the adjudicator used to ignore it and
        // evaluate against today.
        await controller.ClaimSubmit(PasBundles.Complete(
            coverageTypeCode: "CI", servicedStart: "2026-11-02", servicedEnd: null));

        basis!.LineOfBusiness.Should().Be(CloudHealthOffice.PriorAuthRuleEngine.Domain.PaLineOfBusiness.Commercial);
        basis.RequestingProviderNpi.Should().Be(PasBundles.RequestingNpi);
        basis.ServicingProviderNpi.Should().BeNull();
        basis.ServiceDate.Should().Be(new DateOnly(2026, 11, 2), "the requested service date, not today");
        basis.ProcedureCodes.Should().Equal(new PasProcedureCode(PasBundles.CptSystem, "27447"));
        basis.MemberId.Should().Be(PasBundles.MemberId);
        basis.PatientDateOfBirth.Should().Be(new DateOnly(1984, 3, 7));
        basis.DiagnosisCodes.Should().Equal("M17.11");
        _recorder.Last!.LineOfBusiness.Should().Be("Commercial");
        _recorder.Last.Decision!.LineOfBusiness.Should().Be("Commercial");
        _recorder.Last.Decision.StateCode.Should().Be("TX");
    }

    [Fact]
    public async Task UnknownLineOfBusiness_IsPendedWithItsReason_WithoutAdjudication()
    {
        var claimResponse = ClaimResponseOf(await Controller().ClaimSubmit(PasBundles.Complete(coverageTypeCode: "ZZ")));

        AdjudicatorNeverRan();
        claimResponse.Disposition.Should().Be("pended");
        var decision = _recorder.Last!.Decision!;
        decision.Outcome.Should().Be("Pended");
        decision.Rule.Should().Be(PasController.LineOfBusinessUndeterminedRule);
        decision.PendReason.Should().Be(PasController.LineOfBusinessUndeterminedPendReason);
        decision.LineOfBusiness.Should().BeNull("no rule was evaluated on a line of business");
        decision.StateCode.Should().BeNull();
    }

    [Fact]
    public async Task APendReasonFromTheAdjudicator_IsRecorded()
    {
        await Controller(new PasDecisionResult
        {
            HasDecision = false, RuleName = "StateUndetermined",
            PendReason = PasAutoAdjudicator.StateUndeterminedPendReason, LineOfBusiness = "Medicaid",
        }).ClaimSubmit(PasBundles.Complete());

        _recorder.Last!.Decision!.PendReason.Should().Be(PasAutoAdjudicator.StateUndeterminedPendReason);
    }

    // ── D-08 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://example.org/prio\r\nFORGED", "stat", "U")]
    [InlineData("http://example.org/priority", "P1", "E")]
    public async Task NonstandardOrUnrecognisedPriority_IsLoggedAtWarning_Sanitized(string system, string code, string level)
    {
        var logger = new CapturingLogger<PasController>();
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Priority = new CodeableConcept { Coding = [new Coding(system, code)] };

        await Controller(logger: logger).ClaimSubmit(bundle);

        _recorder.Last!.LevelOfService.Should().Be(level);
        var warning = logger.Entries.Should().ContainSingle(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Claim.priority")).Subject;
        warning.Message.Should().Contain(code);
        warning.Message.Should().NotContain("\r").And.NotContain("\n", "values from the request are sanitized");
        warning.Message.Should().Contain("http://example.org/prio");
    }

    [Theory]
    [InlineData("stat", "U")]
    [InlineData("deferred", "E")]
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
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
/// D-32 follow-up: <c>$inquire</c>'s caller is read from the JWT claim names
/// (<c>sub</c>, then <c>client_id</c>, then <c>azp</c>). Neither scheme maps
/// inbound claims any more, so a <c>NameIdentifier</c> claim is not a token's
/// subject and is never taken as the caller.
/// </summary>
public class PasInquireCallerTests
{
    [Theory]
    [InlineData(new[] { "sub", "user-1", "azp", "client-x" }, "user-1")]
    [InlineData(new[] { System.Security.Claims.ClaimTypes.NameIdentifier, "mapped-subject", "azp", "client-x" }, "client-x")]
    [InlineData(new[] { System.Security.Claims.ClaimTypes.NameIdentifier, "mapped-subject" }, null)]
    public async Task Caller_IsReadFromJwtClaimNamesOnly(string[] claims, string? expected)
    {
        string? caller = "unset";
        var inquiry = new Mock<IPriorAuthorizationInquiryService>();
        inquiry.Setup(i => i.FromInquiryClaim(It.IsAny<Claim>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Claim, string, string?>((_, _, c) => caller = c)
            .Returns(new PriorAuthorizationInquiryRequest { TenantId = "tenant-a" });
        inquiry.Setup(i => i.InquireAsync(It.IsAny<PriorAuthorizationInquiryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorAuthorizationInquiryResult.Refused(PriorAuthorizationInquiryOutcome.NotFound));

        var controller = new PasController(
            new Mock<IPasAutoAdjudicator>().Object, new PasResponseBuilder(),
            new Mock<ICms0057ComplianceChecker>().Object, inquiry.Object,
            Options.Create(new PasAutoAdjudicationConfig()), new Mock<IHttpClientFactory>().Object,
            new FakePasAuthorizationRecorder(), NullLogger<PasController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            claims.Chunk(2).Select(c => new System.Security.Claims.Claim(c[0], c[1])), "Bearer"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var bundle = new Bundle
        {
            Entry =
            [
                new()
                {
                    Resource = new Claim
                    {
                        Use = ClaimUseCode.Preauthorization,
                        Identifier = [new Identifier("http://example.org/pas", "PAS-20261011-AAAA0001")],
                    },
                },
            ],
        };

        await controller.ClaimInquire(bundle);

        caller.Should().Be(expected);
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

    private static PasAuthorizationRecordRequest Request(string outcome = "Approved") => new()
    {
        AuthorizationNumber = "PAS-20261011-ABCDEF01",
        Decision = new PasAutomatedDecision { Outcome = outcome, DecidedAt = new DateTime(2026, 10, 11, 9, 30, 1, DateTimeKind.Utc) },
    };

    /// <summary>The requestHash the recorder put on the wire.</summary>
    private static async Task<string?> SentHash(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("requestHash", out var hash) ? hash.GetString() : null;
    }

    // ── SF2: idempotent recording ──────────────────────────────────────

    [Fact]
    public async Task EveryRecord_CarriesAHashOfItsPayload_SameRequestSameHash()
    {
        var sent = new List<string?>();
        var (recorder, _) = Recorder(async r =>
        {
            sent.Add(await SentHash(r));
            return await Json(HttpStatusCode.Created, new { tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved" });
        });

        await recorder.RecordAsync(Request(), Tenant);
        await recorder.RecordAsync(Request(), Tenant);
        await recorder.RecordAsync(Request("Denied"), Tenant);

        sent.Should().HaveCount(3);
        sent[0].Should().MatchRegex("^[a-f0-9]{64}$");
        sent[1].Should().Be(sent[0], "the same request hashes the same, so a retry is recognised");
        sent[2].Should().NotBe(sent[0], "a different request is a different hash");
    }

    [Fact]
    public async Task WriteAnswerLost_TheRecordIsReadBack_AndMatchesItsHash_IsRecorded()
    {
        string? hash = null;
        var calls = new List<string>();
        var (recorder, _) = Recorder(async r =>
        {
            calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");
            if (r.Method == HttpMethod.Post)
            {
                hash = await SentHash(r);
                throw new TaskCanceledException("timed out after the write");
            }

            r.Headers.Authorization!.Scheme.Should().Be("Bearer");
            return await Json(HttpStatusCode.OK, new
            {
                tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved", requestHash = hash,
            });
        });

        var outcome = await recorder.RecordAsync(Request(), Tenant);

        outcome.Recorded.Should().BeTrue("the write did succeed: the record exists and is this request's");
        outcome.Stored!.Status.Should().Be(PriorAuthorizationStatus.Approved);
        calls.Should().Equal(
            "POST /" + HttpPasAuthorizationRecorder.Path,
            "GET /" + HttpPasAuthorizationRecorder.Path + "/PAS-20261011-ABCDEF01");
    }

    [Fact]
    public async Task Answered5xx_TheRecordIsReadBack_AndMatches_IsRecorded()
    {
        string? hash = null;
        var (recorder, _) = Recorder(async r =>
        {
            if (r.Method == HttpMethod.Post)
            {
                hash = await SentHash(r);
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }
            return await Json(HttpStatusCode.OK, new
            {
                tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Pended", requestHash = hash,
            });
        });

        var outcome = await recorder.RecordAsync(Request(), Tenant);

        outcome.Recorded.Should().BeTrue();
        outcome.Stored!.Status.Should().Be(PriorAuthorizationStatus.Pended);
    }

    public static TheoryData<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> FailedReadBacks => new()
    {
        { "not found", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) },
        { "another request's record", _ => Json(HttpStatusCode.OK, new
            {
                tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved",
                requestHash = new string('0', 64),
            }) },
        { "a record with no hash", _ => Json(HttpStatusCode.OK, new
            {
                tenantId = Tenant, authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved",
            }) },
        { "another tenant's record", _ => Json(HttpStatusCode.OK, new
            {
                tenantId = "tenant-b", authorizationNumber = "PAS-20261011-ABCDEF01", status = "Approved",
            }) },
        { "the read fails too", _ => throw new HttpRequestException("still down") },
        { "unreadable", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json") }) },
    };

    [Theory]
    [MemberData(nameof(FailedReadBacks))]
    public async Task WriteAnswerLost_AndTheReadBackDoesNotFindThisRequest_IsUnavailable(
        string because, Func<HttpRequestMessage, Task<HttpResponseMessage>> readBack)
    {
        var reads = 0;
        var (recorder, _) = Recorder(r =>
        {
            if (r.Method == HttpMethod.Post)
                throw new TaskCanceledException("timed out");
            reads++;
            return readBack(r);
        });

        var outcome = await recorder.RecordAsync(Request(), Tenant);

        outcome.Failure.Should().Be(PasRecordFailure.Unavailable, because);
        reads.Should().Be(1, "one compensating read, no more");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Rejected_IsNotReadBack(HttpStatusCode status)
    {
        var methods = new List<HttpMethod>();
        var (recorder, _) = Recorder(r =>
        {
            methods.Add(r.Method);
            return Task.FromResult(new HttpResponseMessage(status));
        });

        (await recorder.RecordAsync(Request(), Tenant)).Failure.Should().Be(PasRecordFailure.Rejected);
        methods.Should().Equal(HttpMethod.Post);
    }

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

/// <summary>Collects formatted log entries for assertions.</summary>
internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
