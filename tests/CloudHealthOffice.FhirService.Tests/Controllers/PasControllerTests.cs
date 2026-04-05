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

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class PasControllerTests
{
    private readonly Mock<IPasAutoAdjudicator> _adjudicatorMock;
    private readonly PasResponseBuilder _responseBuilder;
    private readonly Mock<ICms0057ComplianceChecker> _complianceCheckerMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<PasController>> _loggerMock;
    private readonly PasController _controller;

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

        var verificationClient = new HttpClient(new NoOpHandler())
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
            config,
            _httpClientFactoryMock.Object,
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
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ValidAutoApprovableRequest_CompletesWithin15Seconds()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
        claimResponse.Error.Should().NotBeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ComplexRequest_ReturnsPendedClaimResponse()
    {
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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

    private static Bundle CreateRequestBundle(string procedureCode)
    {
        var claim = new Claim
        {
            Id = Guid.NewGuid().ToString(),
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Insurer = new ResourceReference("Organization/cho-payer"),
            Provider = new ResourceReference("Practitioner/1234567890"),
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            Insurance = new List<Claim.InsuranceComponent>
            {
                new() { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") }
            },
            Item = new List<Claim.ItemComponent>
            {
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept(
                        "http://www.ama-assn.org/go/cpt", procedureCode),
                }
            },
        };

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent>
            {
                new() { Resource = claim }
            },
        };
    }

    /// <summary>
    /// No-op HTTP handler that returns 201 for auth-service persistence calls.
    /// </summary>
    private class NoOpHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created));
        }
    }
}
