using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// G-44. CRD answered with free-text cards only: an EHR could display CHO's
/// determination to a human, but nothing downstream could branch on it. These
/// tests cover the machine-readable half — the Da Vinci CRD
/// <c>coverage-information</c> extension, carried on the draft order in a CDS
/// Hooks system action.
///
/// <para>
/// Two kinds of assertion here, and the second is the one that matters. The
/// first is that CHO emits what it determined. The second is that what it emits
/// is <em>conformant</em>: the extension has nine invariants in CRD 2.2.1, most
/// of which forbid shapes that look perfectly reasonable, and a server that
/// quietly violates one produces a resource a validator rejects and an EHR may
/// refuse to store. They are asserted against the emitted JSON rather than
/// trusted to the construction code.
/// </para>
/// </summary>
public class CrdCoverageInformationTests
{
    private const string Tenant = "tenant-crd-coverage-information";
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string ExtensionUrl = CrdCoverageInformation.ExtensionUrl;

    private readonly Mock<IPriorAuthRuleEngine> _engine = new();

    private static readonly CrdConfig Config = new()
    {
        Enabled = true,
        AuthRequiredCodes = ["27447"],
        AutoApprovedCodes = ["99213"],
        DocumentationRequiredCodes = ["72148"],
    };

    public CrdCoverageInformationTests()
    {
        // The engine abstains throughout: these tests are about how a
        // determination is expressed, not about how it is reached.
        _engine
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "platform/TX/Medicaid/STAR",
            });
    }

    // ── What CHO asserts ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("27447", "covered", "auth-needed")]
    [InlineData("99213", "covered", "no-auth")]
    [InlineData("72148", "covered", "indeterminate")]
    [InlineData("99999", "indeterminate", "indeterminate")]
    public async Task The_extension_states_the_determination_the_card_describes(
        string code, string expectedCovered, string expectedPaNeeded)
    {
        var extension = await OnlyExtensionAsync(code);

        Part(extension, "covered").Should().Be(expectedCovered);
        Part(extension, "pa-needed").Should().Be(expectedPaNeeded);
    }

    /// <summary>
    /// A code classified as needing documentation says nothing about prior
    /// authorization. Emitting <c>no-auth</c> there would be the fail-open
    /// behaviour D-21 was raised about, re-introduced in a field no human reads.
    /// </summary>
    [Fact]
    public async Task A_documentation_only_classification_does_not_assert_no_authorization_needed()
    {
        var extension = await OnlyExtensionAsync("72148");

        Part(extension, "pa-needed").Should().NotBe("no-auth");
        Part(extension, "doc-needed").Should().Be("clinical");
    }

    [Fact]
    public async Task The_billing_code_the_determination_applies_to_is_named()
    {
        var extension = await OnlyExtensionAsync("27447");
        var billing = Sub(extension, "billingCode")!.Value.GetProperty("valueCoding");

        billing.GetProperty("code").GetString().Should().Be("27447");
        billing.GetProperty("system").GetString().Should().Be(CptSystem);
    }

    [Fact]
    public async Task Every_assertion_carries_the_four_fields_the_extension_requires()
    {
        var extension = await OnlyExtensionAsync("27447");

        // coverage, covered, date and coverage-assertion-id are all 1..1.
        Sub(extension, "coverage")!.Value.GetProperty("valueReference").GetProperty("reference")
            .GetString().Should().Be("Coverage/cov-001");
        Part(extension, "covered").Should().NotBeNull();
        Sub(extension, "date")!.Value.GetProperty("valueDate").GetString()
            .Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));
        Sub(extension, "coverage-assertion-id")!.Value.GetProperty("valueString").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// "Nothing classifies this code" and "CHO could not evaluate it" are both
    /// indeterminate, and only the second is the IG's <c>technical</c>. A reader
    /// deciding whether to retry needs to be able to tell them apart.
    /// </summary>
    [Fact]
    public async Task An_engine_that_could_not_answer_is_reported_as_technical()
    {
        _engine
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("engine down"));

        var reason = Sub(await OnlyExtensionAsync("99999"), "reason")!.Value
            .GetProperty("valueCodeableConcept");

        reason.GetProperty("coding")[0].GetProperty("code").GetString().Should().Be("technical");
    }

    [Fact]
    public async Task An_unclassified_code_is_not_reported_as_a_technical_failure()
    {
        var reason = Sub(await OnlyExtensionAsync("99999"), "reason")!.Value
            .GetProperty("valueCodeableConcept");

        reason.TryGetProperty("coding", out _).Should().BeFalse(
            "nothing in the IG's reason value set means \"this payer has no classification for the code\", "
            + "and a coding CHO cannot justify would be a wrong claim rather than a missing one");
        reason.GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── Conformance: the invariants ──────────────────────────────────────────

    [Theory]
    [InlineData("27447")]
    [InlineData("99213")]
    [InlineData("72148")]
    [InlineData("99999")]
    public async Task Every_determination_satisfies_every_invariant(string code)
    {
        AssertInvariants(await OnlyExtensionAsync(code));
    }

    /// <summary>
    /// The CRD 2.2.1 invariants on <c>ext-coverage-information</c>, asserted on
    /// the emitted JSON. Named individually so a failure says which rule broke
    /// rather than only that something did.
    /// </summary>
    private static void AssertInvariants(JsonElement extension)
    {
        var covered = Part(extension, "covered");
        var paNeeded = Part(extension, "pa-needed");
        var docNeeded = Part(extension, "doc-needed");
        var docPurpose = Part(extension, "doc-purpose");
        var hasReason = Sub(extension, "reason") is not null;

        // q1 — a questionnaire needs a doc-needed beside it.
        if (Sub(extension, "questionnaire") is not null)
            docNeeded.Should().NotBeNull("crd-ci-q1");

        // q2 — a not-covered assertion says nothing about prior authorization.
        if (covered == "not-covered")
            paNeeded.Should().BeNull("crd-ci-q2");

        // q3 — conditional anywhere requires info-needed.
        if (covered == "conditional" || paNeeded == "conditional" || docNeeded == "conditional")
            Sub(extension, "info-needed").Should().NotBeNull("crd-ci-q3");

        // q4 — withpa is incompatible with a satisfied or no-auth determination.
        if (paNeeded is "satisfied" or "no-auth" || covered == "not-covered")
            docPurpose.Should().NotBe("withpa", "crd-ci-q4");

        // q5 — satisfied-pa-id exists exactly when pa-needed is satisfied.
        (Sub(extension, "satisfied-pa-id") is not null)
            .Should().Be(paNeeded == "satisfied", "crd-ci-q5");

        // q6 — an OTH info-needed has to be explained.
        if (Part(extension, "info-needed") == "OTH")
            hasReason.Should().BeTrue("crd-ci-q6");

        // q7 — a reason that is not from the value set must carry text. CHO
        // always carries text, so this holds whatever the coding is.
        if (Sub(extension, "reason") is { } reason)
        {
            reason.GetProperty("valueCodeableConcept").TryGetProperty("text", out var text)
                .Should().BeTrue("crd-ci-q7");
            text.GetString().Should().NotBeNullOrWhiteSpace("crd-ci-q7");
        }

        // q8 — a doc-purpose other than conditional has to be explained.
        if (docPurpose is not null && docPurpose != "conditional")
            hasReason.Should().BeTrue("crd-ci-q8");

        // q9 — an indeterminate answer has to say why it could not be decided.
        if (covered == "indeterminate" || paNeeded == "indeterminate" || docNeeded == "indeterminate")
            hasReason.Should().BeTrue("crd-ci-q9");

        // The required slices, which no invariant states because cardinality does.
        foreach (var required in new[] { "coverage", "covered", "date", "coverage-assertion-id" })
            Sub(extension, required).Should().NotBeNull($"'{required}' is 1..1");
    }

    // ── The system action around it ──────────────────────────────────────────

    /// <summary>
    /// A system action asks the EHR to store this resource. Rebuilding it from
    /// CHO's parsed model would hand back a draft order that had quietly lost
    /// its subject, its requester and its code text — a data-loss bug dressed as
    /// a conformance feature.
    /// </summary>
    [Fact]
    public async Task The_order_returned_is_the_ehrs_own_order_with_nothing_removed()
    {
        var request = Hook("27447", coverageId: "cov-001");
        request.Context!.DraftOrders!.Entry![0].RawResource = JsonDocument.Parse("""
            {
              "resourceType": "ServiceRequest",
              "id": "sr-42",
              "status": "draft",
              "intent": "order",
              "subject": { "reference": "Patient/pat-001" },
              "requester": { "reference": "Practitioner/123" },
              "code": {
                "text": "Total knee arthroplasty",
                "coding": [ { "system": "http://www.ama-assn.org/go/cpt", "code": "27447" } ]
              },
              "extension": [ { "url": "http://example.org/ehr-local", "valueString": "keep me" } ]
            }
            """).RootElement.Clone();

        var resource = (await Service().EvaluateCoverageRequirementsAsync(request, Tenant))
            .SystemActions.Should().ContainSingle().Subject.Resource!.Value;

        resource.GetProperty("id").GetString().Should().Be("sr-42");
        resource.GetProperty("status").GetString().Should().Be("draft");
        resource.GetProperty("subject").GetProperty("reference").GetString().Should().Be("Patient/pat-001");
        resource.GetProperty("requester").GetProperty("reference").GetString().Should().Be("Practitioner/123");
        resource.GetProperty("code").GetProperty("text").GetString().Should().Be("Total knee arthroplasty");

        var extensions = resource.GetProperty("extension").EnumerateArray().ToList();
        extensions.Should().Contain(e => e.GetProperty("url").GetString() == "http://example.org/ehr-local",
            "the EHR's own extensions survive");
        extensions.Should().Contain(e => e.GetProperty("url").GetString() == ExtensionUrl);
    }

    [Fact]
    public async Task The_action_is_an_update_of_the_order_it_is_about()
    {
        var action = (await Service().EvaluateCoverageRequirementsAsync(Hook("27447"), Tenant))
            .SystemActions.Should().ContainSingle().Subject;

        action.Type.Should().Be("update");
        action.Resource!.Value.GetProperty("resourceType").GetString().Should().Be("ServiceRequest");
    }

    /// <summary>
    /// The extension requires a Coverage reference. CHO reads one from the
    /// prefetch it has always declared, and when there is none it says nothing
    /// rather than naming a Coverage it did not resolve — a fabricated reference
    /// inside a resource the EHR is being asked to store is worse than the
    /// card-only answer this replaces.
    /// </summary>
    [Fact]
    public async Task Without_a_resolvable_coverage_cho_asserts_nothing_and_still_answers_with_a_card()
    {
        var request = Hook("27447");
        request.Prefetch = null;

        var result = await Service().EvaluateCoverageRequirementsAsync(request, Tenant);

        result.SystemActions.Should().BeEmpty();
        result.Cards.Should().ContainSingle().Which.Summary.Should().Contain("Prior authorization required");
    }

    [Fact]
    public void A_cards_only_response_omits_the_system_actions_member_entirely()
    {
        // An EHR that never saw systemActions before must see the same bytes.
        var json = JsonSerializer.Serialize(new CrdCardResponse
        {
            Cards = [CrdService.BuildRequirementsUnknownCard("a service")],
        });

        json.Should().NotContain("systemActions");
    }

    [Fact]
    public async Task An_order_with_no_coded_service_is_asserted_as_indeterminate_not_skipped()
    {
        var request = Hook("27447");
        request.Context!.DraftOrders!.Entry![0].Resource = new CrdDraftOrderResource
        {
            ResourceType = "ServiceRequest",
            Code = new CrdCodeableConcept { Coding = [] },
        };

        var extension = Extensions(
            (await Service().EvaluateCoverageRequirementsAsync(request, Tenant)).SystemActions)
            .Should().ContainSingle().Subject;

        Part(extension, "covered").Should().Be("indeterminate");
        AssertInvariants(extension);
    }

    [Fact]
    public async Task Two_draft_orders_each_get_their_own_action()
    {
        var request = Hook("27447");
        request.Context!.DraftOrders!.Entry!.Add(new CrdDraftOrderEntry
        {
            Resource = new CrdDraftOrderResource
            {
                ResourceType = "ServiceRequest",
                Code = new CrdCodeableConcept
                {
                    Coding = [new() { System = CptSystem, Code = "99213", Display = "99213" }],
                },
            },
        });

        var actions = (await Service().EvaluateCoverageRequirementsAsync(request, Tenant)).SystemActions;

        actions.Should().HaveCount(2);
        Extensions(actions).Select(e => Part(e, "pa-needed"))
            .Should().BeEquivalentTo(["auth-needed", "no-auth"]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CrdService Service() => new(
        new UnavailableTerminology(),
        _engine.Object,
        Options.Create(Config),
        new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 })),
        NullLogger<CrdService>.Instance);

    private async Task<JsonElement> OnlyExtensionAsync(string code)
    {
        var result = await Service().EvaluateCoverageRequirementsAsync(Hook(code), Tenant);
        return Extensions(result.SystemActions).Should().ContainSingle().Subject;
    }

    private static List<JsonElement> Extensions(IEnumerable<CdsHooksSystemAction> actions) =>
        actions
            .SelectMany(a => a.Resource!.Value.GetProperty("extension").EnumerateArray())
            .Where(e => e.GetProperty("url").GetString() == ExtensionUrl)
            .ToList();

    private static JsonElement? Sub(JsonElement extension, string url)
    {
        foreach (var part in extension.GetProperty("extension").EnumerateArray())
        {
            if (part.GetProperty("url").GetString() == url)
            {
                return part;
            }
        }

        return null;
    }

    private static string? Part(JsonElement extension, string url)
    {
        if (Sub(extension, url) is not { } part)
        {
            return null;
        }

        return part.TryGetProperty("valueCode", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// Terminology is unavailable throughout: these tests evaluate CPT codes,
    /// which are never translated, and a reachable stub would only add a
    /// dependency the behaviour under test does not have.
    /// </summary>
    private sealed class UnavailableTerminology : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new Handler()) { BaseAddress = new Uri("http://terminology.test/") };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("terminology-service unavailable");
        }
    }

    private static CrdHookRequest Hook(string code, string? coverageId = "cov-001") => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-select",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "pat-001",
            DraftOrders = new CrdDraftOrders
            {
                Entry =
                [
                    new()
                    {
                        Resource = new CrdDraftOrderResource
                        {
                            ResourceType = "ServiceRequest",
                            Code = new CrdCodeableConcept
                            {
                                Coding = [new() { System = CptSystem, Code = code, Display = code }],
                            },
                        },
                    },
                ],
            },
        },
        Prefetch = coverageId is null ? null : new Dictionary<string, JsonElement>
        {
            ["coverage"] = JsonDocument.Parse($$"""
                {
                  "resourceType": "Bundle",
                  "type": "searchset",
                  "entry": [
                    { "resource": { "resourceType": "Coverage", "id": "{{coverageId}}", "status": "active" } }
                  ]
                }
                """).RootElement.Clone(),
        },
    };
}
