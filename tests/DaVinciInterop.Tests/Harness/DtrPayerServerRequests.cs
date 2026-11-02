using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// What the DTR payer-server suite needs to know about the seeded member
/// before it can ask CHO for anything: the id of the member's Coverage, and a
/// CRD <c>coverage-assertion-id</c> CHO issued for an order that needs
/// documentation (the <c>context</c> a DTR client quotes back, spec-107).
/// </summary>
public sealed record DtrSourceData(string CoverageId, string ContextId);

/// <summary>
/// The tester-provided inputs of the Inferno DTR 2.2.0 payer-server suite
/// (<c>dtr_payer_server_v220</c>), built from the seed so every request names
/// the member, coverage and content CHO actually holds.
///
/// <para>
/// The kit requires one <c>$questionnaire-package</c> request of each input
/// type on its own: questionnaire canonicals only, a CRD context id only, and
/// an order only. The canonical request names both of CHO's DTR content forms,
/// the standard MRI form and the adaptive sleep-study form, so the run covers
/// a static package and an adaptive one completed through
/// <c>$next-question</c> with the answers in the QuestionnaireResponse template.
/// </para>
///
/// <para>
/// The error cases are CHO's own rules, not the harness's: an unknown context
/// id is source data CHO cannot use (spec-130), and a response answering the
/// first adaptive question with text where CHO asked yes or no is invalid
/// against the contained Questionnaire CHO issued (spec-147).
/// </para>
/// </summary>
public static class DtrPayerServerRequests
{
    /// <summary>CHO's shipped standard DTR form (DtrContent/Questionnaire-pa-imaging-mri.json).</summary>
    public const string MriQuestionnaire = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0";

    /// <summary>CHO's shipped adaptive DTR form (DtrContent/Questionnaire-pa-sleep-study-adaptive.json).</summary>
    public const string SleepStudyQuestionnaireUrl = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-sleep-study-adaptive";

    public const string SleepStudyQuestionnaireVersion = "1.0.0";

    /// <summary>MRI lumbar spine without contrast: one of the MRI form's focus codes.</summary>
    public const string MriOrderCode = "72148";

    /// <summary>The draft order the order-only request and the CRD call carry.</summary>
    public const string OrderId = "interop-dtr-order-mri";

    /// <summary>A context id CHO never issued.</summary>
    public const string UnknownContextId = "00000000-0000-4000-8000-000000000000";

    /// <summary>The C4BB/plan Organization fhir-service serves (Fhir:PlanOrganization:Id in the interop profile).</summary>
    public const string PlanOrganizationId = "interop-plan";

    public const string PractitionerNpi = "1679576722";

    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string OriginExtension = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/information-origin";
    private const string SleepStudySystem = "https://cloudhealthoffice.com/fhir/CodeSystem/dtr-sleep-study";

    /// <summary>
    /// The three <c>$questionnaire-package</c> requests, as the JSON array the
    /// kit's <c>questionnaire_package_request_parameters</c> input takes.
    /// </summary>
    public static JsonArray PackageRequests(ChoSeedFixture fixture, DtrSourceData source) =>
    [
        Parameters(
            Part("coverage", Coverage(fixture, source.CoverageId)),
            Value("questionnaire", "valueCanonical", MriQuestionnaire),
            Value("questionnaire", "valueCanonical", $"{SleepStudyQuestionnaireUrl}|{SleepStudyQuestionnaireVersion}")),
        Parameters(
            Part("coverage", Coverage(fixture, source.CoverageId)),
            Value("context", "valueString", source.ContextId)),
        Parameters(
            Part("coverage", Coverage(fixture, source.CoverageId)),
            Part("order", Order(fixture, source.CoverageId))),
    ];

    /// <summary>A request whose context id CHO never issued (spec-130).</summary>
    public static JsonObject SourceDataErrorRequest(ChoSeedFixture fixture, DtrSourceData source) => Parameters(
        Part("coverage", Coverage(fixture, source.CoverageId)),
        Value("context", "valueString", UnknownContextId));

    /// <summary>
    /// The answers the kit completes the adaptive form with, keyed by the
    /// contained Questionnaire's canonical: no home test because of
    /// cardiopulmonary disease, then an Epworth score.
    /// </summary>
    public static JsonArray ResponseTemplates() =>
    [
        new JsonObject
        {
            ["resourceType"] = "QuestionnaireResponse",
            ["contained"] = new JsonArray(ContainedSleepStudy()),
            ["questionnaire"] = "#adaptive",
            ["status"] = "completed",
            ["item"] = new JsonArray(
                Answer("hsat-done", new JsonObject { ["valueBoolean"] = false }),
                Answer("hsat-contraindication", new JsonObject
                {
                    ["valueCoding"] = new JsonObject
                    {
                        ["system"] = SleepStudySystem,
                        ["code"] = "cardiopulmonary",
                        ["display"] = "Significant cardiopulmonary disease",
                    },
                }),
                Answer("ess-score", new JsonObject { ["valueInteger"] = 14 })),
        },
    ];

    /// <summary>
    /// A response to the adaptive form whose first question, asked as yes or
    /// no, is answered with text: invalid against the contained Questionnaire.
    /// </summary>
    public static JsonObject InvalidQuestionnaireResponse(ChoSeedFixture fixture, DtrSourceData source)
    {
        var contained = ContainedSleepStudy();
        contained["item"] = new JsonArray(new JsonObject
        {
            ["linkId"] = "hsat-done",
            ["text"] = "Has the patient completed a home sleep apnea test (HSAT) for the current complaint?",
            ["type"] = "boolean",
            ["required"] = true,
        });

        return new JsonObject
        {
            ["resourceType"] = "QuestionnaireResponse",
            ["meta"] = Profile("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse-adapt"),
            ["contained"] = new JsonArray(contained),
            ["extension"] = new JsonArray(
                new JsonObject
                {
                    ["url"] = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-coverage",
                    ["valueReference"] = new JsonObject { ["reference"] = $"Coverage/{source.CoverageId}" },
                },
                new JsonObject
                {
                    ["url"] = "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/intendedUse",
                    ["valueCodeableConcept"] = Concept(
                        "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp", "withpa", "Include with prior authorization request"),
                }),
            ["questionnaire"] = "#adaptive",
            ["status"] = "in-progress",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{fixture.Member.MemberId}" },
            ["authored"] = "2026-11-01T00:00:00Z",
            ["item"] = new JsonArray(Answer("hsat-done", new JsonObject { ["valueString"] = "probably not" })),
        };
    }

    /// <summary>
    /// Reads the member's Coverage id from CHO and has CHO's CRD service decide
    /// an MRI order, returning the coverage-assertion-id it issued. Both go
    /// through CHO's own APIs as a provider's systems would; nothing is
    /// written to CHO's store directly.
    /// </summary>
    public static async Task<DtrSourceData> ObtainAsync(
        ChoSeedFixture fixture, string hostBaseUrl, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());

        var root = hostBaseUrl.TrimEnd('/');
        var search = await http.GetAsync(
            $"{root}/fhir/r4/Coverage?patient={Uri.EscapeDataString(fixture.Member.MemberId)}", cancellationToken);
        var searchBody = await search.Content.ReadAsStringAsync(cancellationToken);
        var coverageId = (JsonNode.Parse(searchBody)?["entry"] as JsonArray)?
            .Select(e => e?["resource"])
            .FirstOrDefault(r => r?["resourceType"]?.GetValue<string>() == "Coverage")?["id"]?.GetValue<string>();
        if (!search.IsSuccessStatusCode || string.IsNullOrEmpty(coverageId))
        {
            throw new InteropEnvironmentException(
                $"CHO answered HTTP {(int)search.StatusCode} to the seeded member's Coverage search, with no " +
                $"Coverage id, so the DTR requests would name a coverage CHO does not hold: {Clip(searchBody)}");
        }

        var discovery = await http.GetAsync($"{root}/cds-services", cancellationToken);
        var discoveryBody = await discovery.Content.ReadAsStringAsync(cancellationToken);
        var hookId = (JsonNode.Parse(discoveryBody)?["services"] as JsonArray)?
            .FirstOrDefault(s => s?["hook"]?.GetValue<string>() == "order-sign")?["id"]?.GetValue<string>()
            ?? throw new InteropEnvironmentException(
                $"CHO's CDS Hooks discovery lists no order-sign service: {Clip(discoveryBody)}");

        var hook = await http.PostAsync(
            $"{root}/cds-services/{hookId}",
            new StringContent(CrdOrderSign(fixture, coverageId).ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);
        var hookBody = await hook.Content.ReadAsStringAsync(cancellationToken);
        var contextId = hook.IsSuccessStatusCode ? AssertionId(hookBody) : null;
        if (contextId is null)
        {
            throw new InteropEnvironmentException(
                $"CHO's CRD order-sign service answered HTTP {(int)hook.StatusCode} without a documentation-needed " +
                $"coverage-information carrying a coverage-assertion-id for CPT {MriOrderCode}, so there is no " +
                $"context id for the DTR context-only request: {Clip(hookBody)}");
        }

        return new DtrSourceData(coverageId, contextId);
    }

    /// <summary>The coverage-assertion-id of the first documentation-needed coverage-information in a CRD response.</summary>
    public static string? AssertionId(string cdsHooksResponse)
    {
        var actions = (JsonNode.Parse(cdsHooksResponse)?["systemActions"] as JsonArray) ?? [];
        foreach (var info in actions
                     .SelectMany(a => (a?["resource"]?["extension"] as JsonArray) ?? [])
                     .Where(e => e?["url"]?.GetValue<string>() ==
                                 "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information"))
        {
            var parts = (info!["extension"] as JsonArray) ?? [];
            var docNeeded = parts.Any(p => p?["url"]?.GetValue<string>() == "doc-needed");
            var id = parts.FirstOrDefault(p => p?["url"]?.GetValue<string>() == "coverage-assertion-id")?["valueString"]
                ?.GetValue<string>();
            if (docNeeded && !string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }

    /// <summary>The CRD order-sign request for the MRI order, with the member's Coverage prefetched.</summary>
    public static JsonObject CrdOrderSign(ChoSeedFixture fixture, string coverageId) => new()
    {
        ["hookInstance"] = Guid.NewGuid().ToString(),
        ["hook"] = "order-sign",
        ["context"] = new JsonObject
        {
            ["userId"] = "Practitioner/interop-dtr-practitioner",
            ["patientId"] = fixture.Member.MemberId,
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(new JsonObject { ["resource"] = Order(fixture, coverageId) }),
            },
        },
        ["prefetch"] = new JsonObject
        {
            ["coverage"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(new JsonObject { ["resource"] = Coverage(fixture, coverageId) }),
            },
        },
    };

    /// <summary>The member's Coverage with CHO's plan, in the shape CRD 2.2 profiles it.</summary>
    public static JsonObject Coverage(ChoSeedFixture fixture, string coverageId) => new()
    {
        ["resourceType"] = "Coverage",
        ["id"] = coverageId,
        ["meta"] = Profile("http://hl7.org/fhir/us/davinci-crd/StructureDefinition/profile-coverage"),
        ["identifier"] = new JsonArray(new JsonObject
        {
            ["type"] = Concept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB", "Member Number"),
            ["system"] = "urn:cho:interop:member",
            ["value"] = fixture.Member.MemberId,
        }),
        ["status"] = "active",
        ["subscriberId"] = fixture.Member.MemberId,
        ["beneficiary"] = new JsonObject { ["reference"] = $"Patient/{fixture.Member.MemberId}" },
        ["relationship"] = Concept("http://terminology.hl7.org/CodeSystem/subscriber-relationship", "self", "Self"),
        ["period"] = new JsonObject { ["start"] = "2026-01-01" },
        ["payor"] = new JsonArray(new JsonObject
        {
            ["reference"] = $"Organization/{PlanOrganizationId}",
            ["display"] = "Interop Health Plan (synthetic)",
        }),
    };

    /// <summary>The provider's draft MRI order, in the shape CRD 2.2 profiles a ServiceRequest.</summary>
    public static JsonObject Order(ChoSeedFixture fixture, string coverageId) => new()
    {
        ["resourceType"] = "ServiceRequest",
        ["id"] = OrderId,
        ["meta"] = Profile("http://hl7.org/fhir/us/davinci-crd/StructureDefinition/profile-servicerequest"),
        ["status"] = "draft",
        ["intent"] = "order",
        ["code"] = Concept(CptSystem, MriOrderCode, "MRI lumbar spine without contrast"),
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{fixture.Member.MemberId}" },
        ["authoredOn"] = "2026-11-01",
        ["requester"] = new JsonObject
        {
            ["identifier"] = new JsonObject { ["system"] = "http://hl7.org/fhir/sid/us-npi", ["value"] = PractitionerNpi },
            ["display"] = "Ordering practitioner (synthetic)",
        },
        ["insurance"] = new JsonArray(new JsonObject { ["reference"] = $"Coverage/{coverageId}" }),
    };

    private static JsonObject ContainedSleepStudy() => new()
    {
        ["resourceType"] = "Questionnaire",
        ["id"] = "adaptive",
        ["meta"] = Profile("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaire-adapt"),
        ["extension"] = new JsonArray(new JsonObject
        {
            ["url"] = "http://hl7.org/fhir/uv/sdc/StructureDefinition/sdc-questionnaire-questionnaireAdaptive",
            ["valueUrl"] = ChoUnderTest.ContainerFhirBaseUrl,
        }),
        ["url"] = SleepStudyQuestionnaireUrl,
        ["version"] = SleepStudyQuestionnaireVersion,
        ["name"] = "PaSleepStudyAdaptive",
        ["title"] = "Prior Authorization: Attended Sleep Study (adaptive)",
        ["status"] = "active",
        ["subjectType"] = new JsonArray("Patient"),
        ["derivedFrom"] = new JsonArray($"{SleepStudyQuestionnaireUrl}|{SleepStudyQuestionnaireVersion}"),
    };

    private static JsonObject Answer(string linkId, JsonObject value)
    {
        value["extension"] = new JsonArray(new JsonObject
        {
            ["url"] = OriginExtension,
            ["extension"] = new JsonArray(new JsonObject
            {
                ["url"] = "source",
                ["valueCode"] = "manual",
            }),
        });
        return new JsonObject { ["linkId"] = linkId, ["answer"] = new JsonArray(value) };
    }

    private static JsonObject Parameters(params JsonObject[] parts) => new()
    {
        ["resourceType"] = "Parameters",
        ["meta"] = Profile("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-qpackage-input-parameters"),
        ["parameter"] = new JsonArray(parts),
    };

    private static JsonObject Part(string name, JsonObject resource) => new() { ["name"] = name, ["resource"] = resource };

    private static JsonObject Value(string name, string type, string value) => new() { ["name"] = name, [type] = value };

    private static JsonObject Profile(string profile) => new() { ["profile"] = new JsonArray(profile) };

    private static JsonObject Concept(string system, string code, string display) => new()
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code, ["display"] = display }),
    };

    private static string Clip(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
