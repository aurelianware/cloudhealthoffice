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
                        ])
                    ]
                }
            ]
        };

        return Ok(cs);
    }

    private static CapabilityStatement.ResourceComponent BuildResource(
        string type,
        (string Name, SearchParamType Type)[] searchParams,
        CapabilityStatement.TypeRestfulInteraction[]? interactions = null)
    {
        interactions ??= [
            CapabilityStatement.TypeRestfulInteraction.Read,
            CapabilityStatement.TypeRestfulInteraction.SearchType,
        ];

        return new CapabilityStatement.ResourceComponent
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
    }
}
