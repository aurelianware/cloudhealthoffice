using FhirService.Services;
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
                        BuildResource("Claim",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            ("created",      SearchParamType.Date),
                            ("status",       SearchParamType.Token),
                            ("use",          SearchParamType.Token)
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
                        BuildResource("Task",
                        [
                            ("_id",          SearchParamType.Token),
                            ("_lastUpdated", SearchParamType.Date),
                            ("patient",      SearchParamType.Reference),
                            // TODO: implement authored-on filter in TaskController.Search
                            // ("authored-on", SearchParamType.Date),
                            ("status",       SearchParamType.Token),
                            ("focus",        SearchParamType.Reference),
                            ("owner",        SearchParamType.Reference)
                        ],
                        supportedProfiles: [FhirAppealMapper.TaskProfileUrl]),
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
                    ]
                }
            ]
        };

        return Ok(cs);
    }

    private static CapabilityStatement.ResourceComponent BuildResource(
        string type,
        (string Name, SearchParamType Type)[] searchParams,
        CapabilityStatement.TypeRestfulInteraction[]? interactions = null,
        string[]? supportedProfiles = null)
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

        return resource;
    }
}
