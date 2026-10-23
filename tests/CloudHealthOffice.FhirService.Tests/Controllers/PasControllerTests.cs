using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using CloudHealthOffice.FhirService.Tests.TestSupport;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class PasControllerTests
{
    private readonly Mock<IPasAutoAdjudicator> _adjudicatorMock;
    private readonly PasResponseBuilder _responseBuilder;
    private readonly Mock<ICms0057ComplianceChecker> _complianceCheckerMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<PasController>> _loggerMock;
    private readonly PasController _controller;

    // Persistence is now a seam of its own (IPasAuthorizationRecorder). The
    // HTTP no-op that used to stand for authorization-service answered 201 to
    // anything, which is how a payload that authorization-service rejects went
    // unnoticed (D-01). The real seam is covered end to end by
    // Integration/PasSubmitPersistenceSeamTests.
    private readonly FakePasAuthorizationRecorder _recorder = new();

    public PasControllerTests()
    {
        _adjudicatorMock = new Mock<IPasAutoAdjudicator>();
        _responseBuilder = new PasResponseBuilder();
        _complianceCheckerMock = new Mock<ICms0057ComplianceChecker>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<PasController>>();

        // Default: compliance passes
        _complianceCheckerMock
            .Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns(new ComplianceResult(
                true,
                Array.Empty<ComplianceIssue>(),
                Array.Empty<ComplianceWarning>(),
                new ComplianceSummary("Claim", 10, 10,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new TimelineCompliance(true))));

        // Default: mock HTTP clients
        var httpClient = new HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://authorization-service.test/"),
        };
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("AuthorizationService"))
            .Returns(httpClient);

        // The complete bundle names the requesting NPI, so verification now
        // runs; 404 = no verdict, and the request proceeds (fail open).
        var verificationClient = new HttpClient(new NoOpHandler(HttpStatusCode.NotFound))
        {
            BaseAddress = new Uri("http://provider-verification-service.test/"),
        };
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("ProviderVerificationService"))
            .Returns(verificationClient);

        var config = Options.Create(new PasAutoAdjudicationConfig
        {
            Enabled = true,
            MaxResponseMs = 12000,
        });

        _controller = new PasController(
            _adjudicatorMock.Object,
            _responseBuilder,
            _complianceCheckerMock.Object,
            // These suites exercise $submit; $inquire has its own coverage.
            new Mock<IPriorAuthorizationInquiryService>().Object,
            config,
            _httpClientFactoryMock.Object,
            _recorder,
            _loggerMock.Object);

        // Set up HttpContext with tenant
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ValidAutoApprovableRequest_ReturnsApprovedClaimResponse()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = true,
                Decision = "approved",
                AuthorizationNumber = "PAS-TEST-001",
                RuleName = "AutoApproveList",
                ElapsedMs = 50,
            });

        var bundle = CreateRequestBundle("99213");
        var result = await _controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");
        claimResponse.PreAuthRef.Should().Be("PAS-TEST-001");
        _recorder.Last!.AuthorizationNumber.Should().Be("PAS-TEST-001", "the preAuthRef names the stored record");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_UnexpectedFailure_AnswersWithATraceId_NotTheExceptionText()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mongodb://admin:hunter2@db:27017 timed out"));
        _controller.HttpContext.TraceIdentifier = "trace-pas-1";

        var result = await _controller.ClaimSubmit(CreateRequestBundle("99213"));

        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var outcome = obj.Value.Should().BeOfType<OperationOutcome>().Subject;
        var diagnostics = outcome.Issue.Single().Diagnostics;
        diagnostics.Should().NotContain("hunter2");
        diagnostics.Should().Contain(System.Diagnostics.Activity.Current?.Id ?? "trace-pas-1");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ValidAutoApprovableRequest_CompletesWithin15Seconds()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = true,
                Decision = "approved",
                AuthorizationNumber = "PAS-TEST-001",
                RuleName = "AutoApproveList",
                ElapsedMs = 50,
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bundle = CreateRequestBundle("99213");
        await _controller.ClaimSubmit(bundle);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(15000);
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_DeniableService_ReturnsDeniedWithReason()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = true,
                Decision = "denied",
                DenialReasonCode = "NOT_COVERED",
                DenialReason = "Service V2020 is not a covered benefit",
                RuleName = "AutoDenyList",
                ElapsedMs = 10,
            });

        var bundle = CreateRequestBundle("V2020");
        var result = await _controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("denied");
        // The denial reason travels as a note, not as ClaimResponse.error — see
        // PasResponseBuilder.DenialNote for why that element is the wrong one.
        claimResponse.ProcessNote.Should().NotBeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ComplexRequest_ReturnsPendedClaimResponse()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = false,
                RuleName = "NoRuleMatch",
                ElapsedMs = 100,
            });

        var bundle = CreateRequestBundle("99999");
        var result = await _controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("pended");
        claimResponse.Outcome.Should().Be(ClaimProcessingCodes.Queued);
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_InvalidBundle_Returns400WithOperationOutcome()
    {
        _complianceCheckerMock
            .Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns(new ComplianceResult(
                false,
                new[] { new ComplianceIssue("error", "MISSING_ELEMENT", "Missing required element: status") },
                Array.Empty<ComplianceWarning>(),
                new ComplianceSummary("Claim", 5, 10,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new TimelineCompliance(false))));

        var bundle = CreateRequestBundle("99213");
        var result = await _controller.ClaimSubmit(bundle);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
        statusResult.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_MissingClaim_Returns400()
    {
        // Bundle with no Claim resource
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent>
            {
                new() { Resource = new Patient { Id = "pat-001" } }
            },
        };

        var result = await _controller.ClaimSubmit(bundle);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ResponseIsPasConformantBundle()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = true,
                Decision = "approved",
                AuthorizationNumber = "PAS-TEST-002",
                RuleName = "AutoApproveList",
            });

        var bundle = CreateRequestBundle("99213");
        var result = await _controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        responseBundle.Type.Should().Be(Bundle.BundleType.Collection);
        responseBundle.Entry.Should().HaveCount(1);

        var claimResponse = responseBundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.Meta.Profile.Should().Contain(
            "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse");
        claimResponse.Use.Should().Be(ClaimUseCode.Preauthorization);
    }

    // A complete PAS Bundle (Claim + Patient + Coverage + Practitioner): $submit
    // now refuses a Claim whose referenced resources are missing instead of
    // storing placeholders (D-03), so a Claim-only bundle no longer reaches
    // the adjudicator.
    private static Bundle CreateRequestBundle(string procedureCode)
        => PasBundles.Complete(procedureCode: procedureCode);

    /// <summary>
    /// No-op HTTP handler answering a fixed status (201 by default) to
    /// anything. Persistence no longer goes through it (see _recorder).
    /// </summary>
    private class NoOpHandler(HttpStatusCode status = HttpStatusCode.Created) : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
