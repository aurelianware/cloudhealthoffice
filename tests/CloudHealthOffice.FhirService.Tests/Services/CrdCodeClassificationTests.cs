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

public class CrdCodeClassificationTests
{
    private CrdService CreateService(CrdConfig? config = null)
    {
        config ??= new CrdConfig
        {
            AuthRequiredCodes = new List<string> { "27447", "E11.9" },
            AutoApprovedCodes = new List<string> { "99213", "99214" },
            DocumentationRequiredCodes = new List<string> { "72148" },
        };

        var httpClientFactory = new Mock<IHttpClientFactory>();
        var terminologyClient = new System.Net.Http.HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://terminology-service.test/"),
        };
        httpClientFactory
            .Setup(f => f.CreateClient("TerminologyService"))
            .Returns(terminologyClient);
        var priorAuthRuleEngine = new Mock<IPriorAuthRuleEngine>();
        priorAuthRuleEngine
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "platform/TX/Medicaid/any",
            });

        return new CrdService(
            httpClientFactory.Object,
            priorAuthRuleEngine.Object,
            Options.Create(config),
            new CrdClassificationStore(CreateMemoryCache()),
            new Mock<ILogger<CrdService>>().Object);
    }

    private static MemoryCache CreateMemoryCache() => new(new MemoryCacheOptions
    {
        SizeLimit = 1024,
    });

    [Fact]
    public async Task EvaluateAsync_UsesHashSetLookup_AuthRequired()
    {
        var service = CreateService();
        var request = CreateHookRequest("http://www.ama-assn.org/go/cpt", "27447");

        var result = await service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("warning");
        result.Cards[0].Summary.Should().Contain("Prior authorization required");
    }

    [Fact]
    public async Task EvaluateAsync_UsesHashSetLookup_AutoApproved()
    {
        var service = CreateService();
        var request = CreateHookRequest("http://www.ama-assn.org/go/cpt", "99213");

        var result = await service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("info");
    }

    [Fact]
    public async Task EvaluateAsync_NoRedis_FallsBackToConfig()
    {
        // No Redis configured — uses in-memory fallback from appsettings CrdConfig
        var service = CreateService();

        // Verify the service works without Redis by exercising the full evaluation path
        var request = CreateHookRequest("http://www.ama-assn.org/go/cpt", "72148");
        var result = await service.EvaluateCoverageRequirementsAsync(request, "test-tenant");

        result.Cards.Should().HaveCount(1);
        result.Cards[0].Indicator.Should().Be("warning");
        result.Cards[0].Summary.Should().Contain("Documentation required");
    }

    [Fact]
    public void SetClassification_UpdatesLookup()
    {
        var service = CreateService(new CrdConfig());

        // Initially no codes configured
        var classification = service.GetClassification("tenant-x");
        classification.AuthRequiredCodes.Should().BeEmpty();

        // Dynamically update
        service.SetClassification("tenant-x", new CrdCodeClassification
        {
            AuthRequiredCodes = new HashSet<string>(StringComparer.Ordinal) { "99999" },
        });

        var updated = service.GetClassification("tenant-x");
        updated.AuthRequiredCodes.Should().Contain("99999");
    }

    private static CrdHookRequest CreateHookRequest(string system, string code) => new()
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
                                    new() { System = system, Code = code, Display = code },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    private class NoOpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
