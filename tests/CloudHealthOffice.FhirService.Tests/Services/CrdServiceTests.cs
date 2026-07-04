using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class CrdServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IPriorAuthRuleEngine> _priorAuthRuleEngineMock;
    private readonly Mock<ILogger<CrdService>> _loggerMock;
    private readonly CrdConfig _config;
    private MockHttpHandler _terminologyHandler = null!;
    private CrdService _service = null!;

    public CrdServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _priorAuthRuleEngineMock = new Mock<IPriorAuthRuleEngine>();
        _loggerMock = new Mock<ILogger<CrdService>>();

        _config = new CrdConfig
        {
            Enabled = true,
            AuthRequiredCodes = new List<string> { "E11.9", "27447" },
            AutoApprovedCodes = new List<string> { "99213", "99214" },
            DocumentationRequiredCodes = new List<string> { "72148" },
        };

        _terminologyHandler = new MockHttpHandler();
        _priorAuthRuleEngineMock
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "platform/TX/Medicaid/any",
            });
        SetupService();
    }

    private void SetupService()
    {
        var httpClient = new HttpClient(_terminologyHandler)
        {
            BaseAddress = new Uri("http://terminology-service.test/"),
        };
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("TerminologyService"))
            .Returns(httpClient);

        _service = new CrdService(
            _httpClientFactoryMock.Object,
            _priorAuthRuleEngineMock.Object,
            Options.Create(_config),
            new CrdClassificationStore(CreateMemoryCache()),
            _loggerMock.Object);
    }

    private static MemoryCache CreateMemoryCache() => new(new MemoryCacheOptions
    {
        SizeLimit = 1024,
    });

    // ── SNOMED translation ───────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_SnomedCode_TranslatesToIcd10()
    {
        _terminologyHandler.ResponseBody = JsonSerializer.Serialize(new[]
        {
            new { result = true, matches = new[] { new { system = "http://hl7.org/fhir/sid/icd-10-cm", code = "E11.9", display = "Type 2 diabetes mellitus" } } },
        });

        var request = CreateHookRequest("http://snomed.info/sct", "73211009", "Diabetes mellitus");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.TranslationsPerformed.Should().Be(1);
        _terminologyHandler.LastRequestUri.Should().Contain("$batch-translate");
    }

    // ── Auth required ────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AuthRequiredCode_ReturnsAuthRequiredCard()
    {
        _terminologyHandler.ResponseBody = JsonSerializer.Serialize(new[]
        {
            new { result = true, matches = new[] { new { system = "http://hl7.org/fhir/sid/icd-10-cm", code = "E11.9", display = "Type 2 diabetes" } } },
        });

        var request = CreateHookRequest("http://snomed.info/sct", "73211009", "Diabetes");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("warning");
        result.Cards[0].Summary.Should().Contain("Prior authorization required");
    }

    [Fact]
    public async Task EvaluateAsync_PriorAuthRuleRequiresReview_ReturnsAuthRequiredCard()
    {
        _priorAuthRuleEngineMock
            .Setup(e => e.EvaluateAsync(
                It.Is<PaRuleContext>(c => c.ProcedureCodes.Contains("K0800")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "TX-STARPLUS-PA-001",
                FiringRuleName = "DME PA Required Above Threshold - STARPlus",
                ResolvedRuleSetKey = "platform/TX/Medicaid/STARPlus",
            });

        var request = CreateHookRequest("http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "K0800", "Power wheelchair");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("warning");
        result.Cards[0].Summary.Should().Contain("Prior authorization required");
        result.Cards[0].Detail.Should().Contain("DME PA Required");
    }

    // ── Auto approved ────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AutoApprovedCode_ReturnsInfoCard()
    {
        var request = CreateHookRequest("http://www.ama-assn.org/go/cpt", "99213", "Office visit");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("info");
        result.Cards[0].Summary.Should().Contain("No prior authorization needed");
        result.TranslationsPerformed.Should().Be(0);
    }

    // ── Documentation required ───────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_DocumentationRequired_ReturnsLaunchSmartAppSuggestion()
    {
        var request = CreateHookRequest("http://www.ama-assn.org/go/cpt", "72148", "Lumbar MRI");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("warning");
        result.Cards[0].Summary.Should().Contain("Documentation required");
        result.Cards[0].Suggestions.Should().NotBeEmpty();
        result.Cards[0].Links.Should().Contain(l => l.Type == "smart");
    }

    // ── Batch translation ────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_MultipleCodes_BatchTranslates()
    {
        _terminologyHandler.ResponseBody = JsonSerializer.Serialize(new[]
        {
            new { result = true, matches = new[] { new { system = "http://hl7.org/fhir/sid/icd-10-cm", code = "E11.9", display = "Type 2 diabetes" } } },
            new { result = true, matches = new[] { new { system = "http://hl7.org/fhir/sid/icd-10-cm", code = "M17.11", display = "Primary OA right knee" } } },
        });

        var request = CreateMultiCodeHookRequest(
            ("http://snomed.info/sct", "73211009", "Diabetes mellitus"),
            ("http://snomed.info/sct", "239873007", "Osteoarthritis of knee"));

        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.CodesEvaluated.Should().Be(2);
        result.TranslationsPerformed.Should().Be(2);
        _terminologyHandler.RequestCount.Should().Be(1); // Single batch call, not two separate calls
    }

    // ── Graceful degradation ─────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_TerminologyServiceDown_GracefulDegradation()
    {
        _terminologyHandler.ShouldThrow = true;

        var request = CreateHookRequest("http://snomed.info/sct", "73211009", "Diabetes mellitus");
        var result = await _service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        // Should not throw — falls back to raw SNOMED code
        result.Should().NotBeNull();
        result.Cards.Should().NotBeEmpty();
        result.TranslationsPerformed.Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CrdHookRequest CreateHookRequest(string system, string code, string display) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-select",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "456",
            DraftOrders = new CrdDraftOrders
            {
                Entry = new List<CrdDraftOrderEntry>
                {
                    new()
                    {
                        Resource = new CrdDraftOrderResource
                        {
                            ResourceType = "ServiceRequest",
                            Code = new CrdCodeableConcept
                            {
                                Coding = new List<CrdCoding>
                                {
                                    new() { System = system, Code = code, Display = display },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    private static CrdHookRequest CreateMultiCodeHookRequest(
        params (string system, string code, string display)[] codes) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-select",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "456",
            DraftOrders = new CrdDraftOrders
            {
                Entry = codes.Select(c => new CrdDraftOrderEntry
                {
                    Resource = new CrdDraftOrderResource
                    {
                        ResourceType = "ServiceRequest",
                        Code = new CrdCodeableConcept
                        {
                            Coding = new List<CrdCoding>
                            {
                                new() { System = c.system, Code = c.code, Display = c.display },
                            },
                        },
                    },
                }).ToList(),
            },
        },
    };

    /// <summary>
    /// Mock HTTP handler for the Terminology Service that captures requests and returns configurable responses.
    /// </summary>
    private class MockHttpHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "[]";
        public bool ShouldThrow { get; set; }
        public string? LastRequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();

            if (ShouldThrow)
                throw new HttpRequestException("Terminology Service is unavailable");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
