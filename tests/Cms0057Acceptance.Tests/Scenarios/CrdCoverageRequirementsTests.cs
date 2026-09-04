using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-01 — CRD (Coverage Requirements Discovery).
///
/// CDS Hooks order-select / order-sign returns a card saying whether prior
/// authorization is required, with the governing rule identified on the card
/// source. Happy path + negative paths, executed against the REAL CrdService
/// and CrdController in Demo/Cho mode (CHO rule store / config classification).
///
/// Traceability:
///   controller  src/services/fhir-service/Controllers/CrdController.cs
///   service     src/services/fhir-service/Services/CrdService.cs
///   store       src/services/fhir-service/Services/CrdClassificationStore.cs
///   qnxt seam   src/services/benefit-plan-service/Adapters/QnxtBenefitPlanAdapter.cs (GAP — GapAdapterTests.PAS01_*)
///
/// Varies: YES. The QNXT benefit adapter that would source per-plan
/// auth-required rules is still a NotImplementedException stub, so this
/// scenario runs against the CHO classification and the stub is asserted
/// separately in GapAdapterTests.
/// </summary>
public class CrdCoverageRequirementsTests
{
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";

    private static CrdController BuildController()
    {
        var config = Options.Create(new CrdConfig
        {
            Enabled = true,
            AuthRequiredCodes = new List<string> { "27447" },          // total knee arthroplasty
            AutoApprovedCodes = new List<string> { "99213" },          // established office visit
            DocumentationRequiredCodes = new List<string> { "72148" }, // lumbar MRI
        });

        // Real CrdService: no-network HTTP factory (CPT codes need no SNOMED
        // translation) + a Pend-only rule engine so the CHO config
        // classification is the deciding path.
        var service = new CrdService(
            new StubHttpClientFactory(),
            new NoOpPriorAuthRuleEngine(),
            config,
            new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions())),
            AcceptanceContext.Logger<CrdService>());

        return new CrdController(service, config, AcceptanceContext.Logger<CrdController>())
            .WithTenant();
    }

    private static CrdHookRequest HookRequest(string hookType, string code) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = hookType,
        FhirServer = "https://ehr.example.com/fhir",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/npi-1234567890",
            PatientId = "pat-001",
            DraftOrders = new CrdDraftOrders
            {
                ResourceType = "Bundle",
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
                                    new() { System = CptSystem, Code = code, Display = code },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_OrderSelect_AuthRequiredService_ReturnsWarningCardWithGoverningRule()
    {
        var controller = BuildController();

        var result = await controller.ExecuteHook(
            "cho-order-select", HookRequest("order-select", "27447"), CancellationToken.None);

        var card = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CrdCardResponse>().Subject
            .Cards.Should().ContainSingle().Subject;

        card.Indicator.Should().Be("warning");
        card.Summary.Should().Contain("Prior authorization required");
        // Governing rule is identified on the card source topic.
        card.Source.Should().NotBeNull();
        card.Source!.Topic!.Code.Should().Be("auth-required");
    }

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_OrderSign_NoAuthRequiredService_ReturnsInfoCard()
    {
        var controller = BuildController();

        var result = await controller.ExecuteHook(
            "cho-order-sign", HookRequest("order-sign", "99213"), CancellationToken.None);

        var card = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CrdCardResponse>().Subject
            .Cards.Should().ContainSingle().Subject;

        card.Indicator.Should().Be("info");
        card.Summary.Should().Contain("No prior authorization");
    }

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_DocumentationRequiredService_LaunchesDtr()
    {
        // The CRD → DTR bridge: a documentation-required code returns a card
        // that offers a DTR launch (this is what PAS-02 picks up).
        var controller = BuildController();

        var result = await controller.ExecuteHook(
            "cho-order-select", HookRequest("order-select", "72148"), CancellationToken.None);

        var card = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CrdCardResponse>().Subject
            .Cards.Should().ContainSingle().Subject;

        card.Summary.Should().Contain("Documentation required");
        card.Links.Should().Contain(l => l.Type == "smart");
    }

    // ── Negative paths ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_Negative_UnknownHookId_Returns404()
    {
        var controller = BuildController();

        var result = await controller.ExecuteHook(
            "unknown-hook", HookRequest("unknown-hook", "27447"), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_Negative_MismatchedHookType_Returns400()
    {
        var controller = BuildController();

        // URL says order-select but the request payload declares order-sign.
        var result = await controller.ExecuteHook(
            "cho-order-select", HookRequest("order-sign", "27447"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
