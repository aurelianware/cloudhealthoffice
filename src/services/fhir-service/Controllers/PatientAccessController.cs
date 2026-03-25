using FhirService.Mappers;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Patient Access API controller — lightweight FHIR R4 endpoints that map CHO
/// internal models to FHIR resources using System.Text.Json serialization.
///
/// Routes under /fhir/r4/ so that SmartScopeEnforcementMiddleware enforces
/// JWT validation and SMART scope checks on all requests.
///
/// Port of the TypeScript patient-access-mapper.ts endpoints.
/// </summary>
[Route("fhir/r4")]
public class PatientAccessController : FhirControllerBase
{
    private readonly IPatientAccessDataProvider _dataProvider;

    public PatientAccessController(IPatientAccessDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    // Coverage and ExplanationOfBenefit search endpoints are provided by
    // CoverageController and ExplanationOfBenefitController respectively.
    // Registering them here would cause AmbiguousMatchException.
}
