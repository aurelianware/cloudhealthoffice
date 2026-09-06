using FhirService.Services;
using FhirService.Services.Cdex;
using FhirService.Services.Clinical;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Returns the FHIR R4 CapabilityStatement at GET /fhir/r4/metadata.
/// Advertises supported resources, interactions, and search parameters.
/// </summary>
[Route("fhir/r4")]
public class MetadataController : FhirControllerBase
{
    private readonly IConfiguration _config;

    public MetadataController(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>GET /fhir/r4/metadata — FHIR CapabilityStatement</summary>
    [HttpGet("metadata")]
    [ProducesResponseType(typeof(CapabilityStatement), 200)]
    public IActionResult GetCapabilityStatement()
    {
        var serverName = _config["Fhir:ServerName"] ?? "CHO FHIR Server";
        var serverVersion = _config["Fhir:ServerVersion"] ?? "1.0.0";

        var cs = new CapabilityStatement
        {
            Id = "cho-fhir-capability",
            Status = PublicationStatus.Active,
            Date = "2025-01-01",
            Kind = CapabilityStatementKind.Instance,
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["application/fhir+json", "application/json"],
            Software = new CapabilityStatement.SoftwareComponent
            {
                Name = serverName,
                Version = serverVersion
            },
            Implementation = new CapabilityStatement.ImplementationComponent
            {
                Description = "Cloud Health Office FHIR R4 facade — CMS-0057-F compliance layer",
                Url = FhirBaseUrl
            },
            Rest =
            [
                new CapabilityStatement.RestComponent
                {
                    Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                    Security = new CapabilityStatement.SecurityComponent
                    {
                        Cors = true,
                        Service =
                        [
                            new CodeableConcept(
                                "http://terminology.hl7.org/CodeSystem/restful-security-service",
                                "SMART-on-FHIR",
                                "OAuth2 using SMART-on-FHIR profile")
                        ]
                    },
                    Resource =
                    [
                        BuildResource("Patient",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("name",         SearchParamType.String),
                            ("family",       SearchParamType.String),
                            ("given",        SearchParamType.String),
                            ("birthdate",    SearchParamType.Date),
                            ("identifier",   SearchParamType.Token),
                            ("gender",       SearchParamType.Token)
                        ]),
                        BuildResource("Coverage",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            ("beneficiary",  SearchParamType.Reference),
                            ("status",       SearchParamType.Token),
                            ("type",         SearchParamType.Token)
                        ]),
                        BuildResource("ExplanationOfBenefit",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            ("created",      SearchParamType.Date),
                            ("type",         SearchParamType.Token),
                            ("status",       SearchParamType.Token)
                        ]),
                        BuildResource("Encounter",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            ("date",         SearchParamType.Date),
                            ("status",       SearchParamType.Token),
                            ("type",         SearchParamType.Token)
                        ]),
                        // Da Vinci PAS operations, both served by PasController:
                        // POST fhir/r4/Claim/$submit and POST fhir/r4/Claim/$inquire.
                        //
                        // OperationDefinition.name and the invoked operation code are
                        // deliberately different for inquiry, and both values below are
                        // taken from the published IG rather than inferred from each
                        // other: PAS names the definition `Claim-inquiry` while its
                        // `code` — the token in the URL — is `inquire`. Every published
                        // PAS version (1.0.0 through 2.2.1) publishes the canonical as
                        // `Claim-inquiry`, so there is no version for which
                        // `Claim-inquire` is correct. See docs/interop/davinci.md,
                        // "The `$inquire` canonical, resolved".
                        BuildResource("Claim",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            ("created",      SearchParamType.Date),
                            ("status",       SearchParamType.Token),
                            ("use",          SearchParamType.Token)
                        ],
                        operations:
                        [
                            ("submit",  "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit"),
                            ("inquire", "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquiry"),
                        ]),
                        BuildResource("Questionnaire",
                        [
                            ("_id",          SearchParamType.Token),
                            ("name",         SearchParamType.String),
                            ("title",        SearchParamType.String),
                            ("status",       SearchParamType.Token)
                        ],
                        [
                            CapabilityStatement.TypeRestfulInteraction.Read,
                            CapabilityStatement.TypeRestfulInteraction.SearchType,
                            CapabilityStatement.TypeRestfulInteraction.Create,
                            CapabilityStatement.TypeRestfulInteraction.Update,
                        ]),
                        BuildResource("QuestionnaireResponse",
                        [
                            ("_id",            SearchParamType.Token),
                            ("questionnaire",  SearchParamType.Reference),
                            ("patient",        SearchParamType.Reference),
                            ("status",         SearchParamType.Token)
                        ],
                        [
                            CapabilityStatement.TypeRestfulInteraction.Read,
                            CapabilityStatement.TypeRestfulInteraction.SearchType,
                            CapabilityStatement.TypeRestfulInteraction.Create,
                        ]),
                        // ── FHIR conformance-resource endpoints ──────────────────
                        // PR 1 ships read+search for StructureDefinition,
                        // CodeSystem, ValueSet, and OperationDefinition —
                        // advertise them here so clients can discover them
                        // programmatically.
                        BuildResource("StructureDefinition",
                        [
                            ("_id", SearchParamType.Token),
                        ]),
                        BuildResource("CodeSystem",
                        [
                            ("_id", SearchParamType.Token),
                        ]),
                        BuildResource("ValueSet",
                        [
                            ("_id", SearchParamType.Token),
                        ]),
                        BuildResource("OperationDefinition",
                        [
                            ("_id", SearchParamType.Token),
                        ]),
                        // ── Appeal projections (PR 3) ─────────────────────
                        // PR 3 adds runtime read + search for the four
                        // appeal-derived FHIR resources, backed by
                        // appeals-service over HTTP via HttpFhirAppealAdapter
                        // (the first IFhirDataAdapter implementation against
                        // a real backing service).
                        // Task serves TWO profiles: the appeal projection, and
                        // the Da Vinci CDex additional-information request on a
                        // pended prior authorization (PAS-07). `code` selects
                        // between them and `identifier` names a CDex request by
                        // its tracking id — both are advertised because both are
                        // actually honoured by TaskController.Search.
                        BuildResource("Task",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            // TODO: implement authored-on filter in TaskController.Search
                            // ("authored-on", SearchParamType.Date),
                            ("status",       SearchParamType.Token),
                            ("code",         SearchParamType.Token),
                            ("identifier",   SearchParamType.Token),
                            ("focus",        SearchParamType.Reference),
                            ("owner",        SearchParamType.Reference)
                        ],
                        supportedProfiles:
                        [
                            FhirAppealMapper.TaskProfileUrl,
                            CdexCanonicalUrls.TaskAttachmentRequestProfile,
                        ]),
                        BuildResource("Communication",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            // TODO: implement sent filter in CommunicationController.Search
                            // ("sent", SearchParamType.Date),
                            ("about",        SearchParamType.Reference)
                        ],
                        supportedProfiles: [FhirAppealMapper.CommunicationProfileUrl]),
                        BuildResource("DocumentReference",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            // TODO: implement type filter in DocumentReferenceController.Search
                            // ("type", SearchParamType.Token),
                            ("related",      SearchParamType.Reference)
                        ],
                        supportedProfiles: [FhirAppealMapper.DocumentReferenceProfileUrl]),
                        BuildResource("ClaimResponse",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            // TODO: implement created filter in ClaimResponseController.Search
                            // ("created", SearchParamType.Date),
                            ("request",      SearchParamType.Reference)
                        ],
                        supportedProfiles: [FhirAppealMapper.ClaimResponseProfileUrl]),
                        // ── USCDI clinical resources (PAT-02) ─────────────
                        // Generated from ClinicalResourceInventory, the same
                        // table ClinicalResourceController routes from and the
                        // Payer-to-Payer import policy classifies against, so
                        // the statement cannot advertise a type with no read
                        // path or omit one that has one. Each entry declares
                        // read + search-type and EXACTLY the search parameters
                        // the controller honours — `subject` appears only for
                        // the types FHIR R4 defines it on.
                        //
                        // No supportedProfile is declared. CHO serves these as
                        // valid FHIR R4 and does not re-shape a prior payer's
                        // clinical content to satisfy US Core invariants, so a
                        // US Core profile URL here would be a label rather than
                        // a conformance claim. See docs/architecture/clinical-fhir.md.
                        .. ClinicalResourceInventory.All.Select(entry =>
                            BuildResource(
                                entry.ResourceType,
                                [.. entry.SearchParameters.Select(p => (p, SearchParamTypeFor(p)))])),
                    ],
                    Operation =
                    [
                        new CapabilityStatement.OperationComponent
                        {
                            Name = "export",
                            Definition = "http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export",
                        },
                        new CapabilityStatement.OperationComponent
                        {
                            Name = AppealSubmitController.OperationName,
                            Definition = "http://fhir.cloudhealthoffice.com/OperationDefinition/cho-appeal-submit",
                        },
                        // Da Vinci CDex $submit-attachment — the response half of
                        // the additional-information round trip on a pended prior
                        // authorization. Advertised because CdexController
                        // genuinely serves POST fhir/r4/$submit-attachment;
                        // nothing broader about CDex is claimed here, because
                        // nothing broader is implemented.
                        new CapabilityStatement.OperationComponent
                        {
                            Name = CdexCanonicalUrls.SubmitAttachmentOperationName,
                            Definition = CdexCanonicalUrls.SubmitAttachmentOperation,
                        },
                    ]
                }
            ]
        };

        return Ok(cs);
    }

    /// <summary>
    /// The FHIR search-parameter type for a clinical parameter name. <c>_id</c>
    /// is a token; <c>patient</c> and <c>subject</c> are references.
    /// </summary>
    private static SearchParamType SearchParamTypeFor(string name) => name switch
    {
        ClinicalResourceInventory.IdParam => SearchParamType.Token,
        _ => SearchParamType.Reference,
    };

    private static CapabilityStatement.ResourceComponent BuildResource(
        string type,
        (string Name, SearchParamType Type)[] searchParams,
        CapabilityStatement.TypeRestfulInteraction[]? interactions = null,
        string[]? supportedProfiles = null,
        (string Name, string Definition)[]? operations = null)
    {
        interactions ??= [
            CapabilityStatement.TypeRestfulInteraction.Read,
            CapabilityStatement.TypeRestfulInteraction.SearchType,
        ];

        var resource = new CapabilityStatement.ResourceComponent
        {
            Type = type,
            Interaction = interactions
                .Select(i => new CapabilityStatement.ResourceInteractionComponent { Code = i })
                .ToList(),
            SearchParam = searchParams
                .Select(p => new CapabilityStatement.SearchParamComponent
                {
                    Name = p.Name,
                    Type = p.Type
                })
                .ToList()
        };

        if (supportedProfiles is { Length: > 0 })
        {
            resource.SupportedProfile = [.. supportedProfiles];
        }

        // Type-level operations. Advertised ONLY where the route genuinely
        // exists — a CapabilityStatement that claims an operation the server
        // does not serve is worse than one that claims nothing.
        if (operations is { Length: > 0 })
        {
            resource.Operation = operations
                .Select(o => new CapabilityStatement.OperationComponent
                {
                    Name = o.Name,
                    Definition = o.Definition,
                })
                .ToList();
        }

        return resource;
    }
}
