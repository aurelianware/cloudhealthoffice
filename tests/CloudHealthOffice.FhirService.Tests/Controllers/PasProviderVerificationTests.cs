using System.Net;
using System.Text.Json;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class PasProviderVerificationTests
{
    private readonly Mock<IPasAutoAdjudicator> _adjudicatorMock;
    private readonly PasResponseBuilder _responseBuilder;
    private readonly Mock<ICms0057ComplianceChecker> _complianceCheckerMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<PasController>> _loggerMock;
    private MockProviderVerificationHandler _verificationHandler = null!;

    public PasProviderVerificationTests()
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

        // Default: auto-adjudicator approves
        _adjudicatorMock
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult
            {
                HasDecision = true,
                Decision = "approved",
                AuthorizationNumber = "PAS-TEST-001",
                RuleName = "AutoApproveList",
            });

        // Auth service mock
        var authClient = new HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://authorization-service.test/"),
        };
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("AuthorizationService"))
            .Returns(authClient);
    }

    private PasController CreateController(MockProviderVerificationHandler handler)
    {
        _verificationHandler = handler;
        var verificationClient = new HttpClient(handler)
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

        var controller = new PasController(
            _adjudicatorMock.Object,
            _responseBuilder,
            _complianceCheckerMock.Object,
            config,
            _httpClientFactoryMock.Object,
            _loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        return controller;
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_ExcludedProvider_ReturnsDenied()
    {
        var handler = new MockProviderVerificationHandler
        {
            ResponseBody = JsonSerializer.Serialize(new
            {
                npi = "1234567890",
                compositeScore = 0,
                rating = "Excluded",
                status = "Excluded",
                flags = new[]
                {
                    new { severity = "critical", source = "OIG/LEIE", code = "EXCLUDED", message = "Provider excluded" },
                },
                verifiedAt = DateTimeOffset.UtcNow,
            }),
        };
        var controller = CreateController(handler);

        var bundle = CreateRequestBundle("99213", "1234567890");
        var result = await controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("denied");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_LowIntegrityScore_LogsWarningButProceeds()
    {
        var handler = new MockProviderVerificationHandler
        {
            ResponseBody = JsonSerializer.Serialize(new
            {
                npi = "1234567890",
                compositeScore = 30,
                rating = "Alert",
                status = "Active",
                flags = Array.Empty<object>(),
                verifiedAt = DateTimeOffset.UtcNow,
            }),
        };
        var controller = CreateController(handler);

        var bundle = CreateRequestBundle("99213", "1234567890");
        var result = await controller.ClaimSubmit(bundle);

        // Should proceed with normal adjudication (approved)
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_VerificationServiceDown_ProceedsGracefully()
    {
        var handler = new MockProviderVerificationHandler { ShouldThrow = true };
        var controller = CreateController(handler);

        var bundle = CreateRequestBundle("99213", "1234567890");
        var result = await controller.ClaimSubmit(bundle);

        // Should proceed with normal adjudication despite service failure
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_VerificationServiceTimeout_ProceedsGracefully()
    {
        var handler = new MockProviderVerificationHandler { ShouldTimeout = true };
        var controller = CreateController(handler);

        var bundle = CreateRequestBundle("99213", "1234567890");
        var result = await controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_HighIntegrityScore_NoInterference()
    {
        var handler = new MockProviderVerificationHandler
        {
            ResponseBody = JsonSerializer.Serialize(new
            {
                npi = "1234567890",
                compositeScore = 90,
                rating = "Clear",
                status = "Active",
                flags = Array.Empty<object>(),
                verifiedAt = DateTimeOffset.UtcNow,
            }),
        };
        var controller = CreateController(handler);

        var bundle = CreateRequestBundle("99213", "1234567890");
        var result = await controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClaimSubmit_NoProviderNpi_SkipsVerification()
    {
        var handler = new MockProviderVerificationHandler();
        var controller = CreateController(handler);

        // Create claim without provider NPI (no Identifier)
        var bundle = CreateRequestBundle("99213", null);
        var result = await controller.ClaimSubmit(bundle);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var responseBundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = responseBundle.Entry[0].Resource as ClaimResponse;
        claimResponse!.Disposition.Should().Be("approved");

        // Verification handler should NOT have been called
        handler.RequestCount.Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Bundle CreateRequestBundle(string procedureCode, string? providerNpi)
    {
        var provider = new ResourceReference("Practitioner/prov-001");
        var claim = new Claim
        {
            Id = Guid.NewGuid().ToString(),
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Insurer = new ResourceReference("Organization/cho-payer"),
            Provider = provider,
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            Insurance = new List<Claim.InsuranceComponent>
            {
                new() { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") },
            },
            Item = new List<Claim.ItemComponent>
            {
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", procedureCode),
                },
            },
        };

        if (!string.IsNullOrEmpty(providerNpi))
        {
            claim.Provider.Identifier = new Identifier
            {
                System = "http://hl7.org/fhir/sid/us-npi",
                Value = providerNpi,
            };
        }

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent>
            {
                new() { Resource = claim },
            },
        };
    }

    private class NoOpHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
    }

    private class MockProviderVerificationHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "{}";
        public bool ShouldThrow { get; set; }
        public bool ShouldTimeout { get; set; }
        public int RequestCount { get; private set; }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ShouldThrow)
                throw new HttpRequestException("Provider Verification Service unavailable");
            if (ShouldTimeout)
                throw new TaskCanceledException("Request timed out");

            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
