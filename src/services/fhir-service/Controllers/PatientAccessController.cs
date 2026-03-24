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

    /// <summary>GET /fhir/r4/Coverage?patient={id} — search Coverage by patient</summary>
    [HttpGet("Coverage")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchCoverage([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patient) && SmartPatientId != null)
            patient = SmartPatientId;

        if (string.IsNullOrEmpty(patient))
            return FhirBadRequest("Coverage search requires 'patient' parameter.");

        var members = await _dataProvider.GetMembersByPatientIdAsync(patient, ct);
        var selfLink = $"{FhirBaseUrl}/Coverage?patient={patient}";
        var bundle = PatientAccessMapper.CoverageToBundle(members, selfLink);
        return Ok(bundle);
    }

    /// <summary>GET /fhir/r4/ExplanationOfBenefit?patient={id} — search EOBs by patient</summary>
    [HttpGet("ExplanationOfBenefit")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchExplanationOfBenefit([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patient) && SmartPatientId != null)
            patient = SmartPatientId;

        if (string.IsNullOrEmpty(patient))
            return FhirBadRequest("ExplanationOfBenefit search requires 'patient' parameter.");

        var payments = await _dataProvider.GetPaymentsByPatientIdAsync(patient, ct);
        var selfLink = $"{FhirBaseUrl}/ExplanationOfBenefit?patient={patient}";
        var bundle = PatientAccessMapper.PaymentsToEobBundle(payments, selfLink);
        return Ok(bundle);
    }
}
