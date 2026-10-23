using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ChoNative;
using Hl7.Fhir.Model;
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
/// source, AND — since 2026-10-23 — a CDS Hooks system action carrying the Da
/// Vinci CRD coverage-information extension, so a consuming system can branch
/// on the determination instead of parsing the card's prose (G-44). Happy path
/// + negative paths, executed against the REAL CrdService, CrdController and
/// CrdContextResolver in Demo/Cho mode (CHO rule store / config
/// classification).
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

    private static CrdController BuildController(bool memberKnown = true)
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
        // The REAL context resolver over a stub member store: CRD now reads the
        // member's state and line of business from CHO's own records rather
        // than evaluating every member against a hard-coded TX / Medicaid /
        // STAR context (D-21, second part).
        var service = new CrdService(
            new StubHttpClientFactory(),
            new NoOpPriorAuthRuleEngine(),
            config,
            new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions())),
            new CrdContextResolver(
                new StubMemberStore(memberKnown), AcceptanceContext.Logger<CrdContextResolver>()),
            AcceptanceContext.Logger<CrdService>());

        return new CrdController(service, config, AcceptanceContext.Logger<CrdController>())
            .WithTenant();
    }

    /// <summary>
    /// The member and coverage CRD reads its context from. Only the fields the
    /// resolver actually uses: a state, a line of business and an active status.
    /// </summary>
    private sealed class StubMemberStore : IFhirDataAdapter
    {
        private readonly bool _known;

        public StubMemberStore(bool known) => _known = known;

        public Task<Patient?> GetPatientAsync(string id, string tenantId, CancellationToken ct = default) =>
            Task.FromResult(_known
                ? new Patient { Id = id, BirthDate = "1962-07-14", Address = [new Address { State = "TX" }] }
                : null);

        public Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
            CoverageSearchParams search, string tenantId, CancellationToken ct = default)
        {
            if (!_known) return Task.FromResult<(IReadOnlyList<Coverage>, int)>(([], 0));

            var coverage = new Coverage
            {
                Id = "cov-001",
                Status = FinancialResourceStatusCodes.Active,
                Type = new CodeableConcept
                {
                    Coding = [new Coding(
                        ChoNativeFhirDataAdapter.LineOfBusinessSystem,
                        PaLineOfBusiness.Medicaid.ToString())],
                },
            };

            return Task.FromResult<(IReadOnlyList<Coverage>, int)>(([coverage], 1));
        }

        public Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
            PatientSearchParams search, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD context resolution reads one member by id.");

        public Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD context resolution searches by member, not by coverage id.");

        public Task<Encounter?> GetEncounterAsync(string id, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD does not read encounters.");

        public Task<(IReadOnlyList<Encounter> Items, int Total)> SearchEncountersAsync(
            EncounterSearchParams search, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD does not read encounters.");

        public Task<Claim?> GetClaimAsync(string id, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD does not read claims.");

        public Task<(IReadOnlyList<Claim> Items, int Total)> SearchClaimsAsync(
            ClaimSearchParams search, string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("CRD does not read claims.");
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
        // What CHO's own discovery document asks the EHR to send. It is where
        // the Coverage reference the coverage-information extension requires
        // comes from.
        Prefetch = new Dictionary<string, JsonElement>
        {
            ["coverage"] = JsonDocument.Parse("""
                {
                  "resourceType": "Bundle",
                  "type": "searchset",
                  "entry": [
                    { "resource": { "resourceType": "Coverage", "id": "cov-001", "status": "active" } }
                  ]
                }
                """).RootElement.Clone(),
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

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_AuthRequiredService_AlsoReturnsAMachineReadableDetermination()
    {
        // The card is prose. This is the half a consuming system can act on.
        var controller = BuildController();

        var result = await controller.ExecuteHook(
            "cho-order-sign", HookRequest("order-sign", "27447"), CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CrdCardResponse>().Subject;

        var action = response.SystemActions.Should().ContainSingle().Subject;
        action.Type.Should().Be("update");

        var extension = action.Resource!.Value.GetProperty("extension").EnumerateArray()
            .Should().ContainSingle(e =>
                e.GetProperty("url").GetString() == CrdCoverageInformation.ExtensionUrl).Subject;

        var parts = extension.GetProperty("extension").EnumerateArray().ToList();

        Value(parts, "covered").Should().Be("covered");
        Value(parts, "pa-needed").Should().Be("auth-needed");
        parts.Should().Contain(p => p.GetProperty("url").GetString() == "coverage-assertion-id");
    }

    [Fact]
    [Trait("Scenario", "PAS-01")]
    public async Task PAS01_UnknownMember_IsNotToldThatNoAuthorizationIsNeeded()
    {
        // The fail-closed half of D-21. An unresolved member used to be
        // evaluated against a hard-coded Texas Medicaid STAR rule set; it is
        // now not evaluated at all, and the determination says why.
        var controller = BuildController(memberKnown: false);

        var result = await controller.ExecuteHook(
            "cho-order-sign", HookRequest("order-sign", "99999"), CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CrdCardResponse>().Subject;

        response.Cards.Should().ContainSingle()
            .Which.Summary.Should().NotContain("No prior authorization");

        var parts = response.SystemActions.Should().ContainSingle().Subject
            .Resource!.Value.GetProperty("extension").EnumerateArray()
            .Single(e => e.GetProperty("url").GetString() == CrdCoverageInformation.ExtensionUrl)
            .GetProperty("extension").EnumerateArray().ToList();

        Value(parts, "pa-needed").Should().Be("indeterminate");
        parts.Single(p => p.GetProperty("url").GetString() == "reason")
            .GetProperty("valueCodeableConcept").GetProperty("coding")[0].GetProperty("code")
            .GetString().Should().Be("no-member-found");
    }

    private static string? Value(IEnumerable<JsonElement> parts, string url) =>
        parts.Where(p => p.GetProperty("url").GetString() == url)
            .Select(p => p.GetProperty("valueCode").GetString())
            .FirstOrDefault();

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
