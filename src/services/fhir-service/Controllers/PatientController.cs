using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Patient resource — read and search.
/// Supports search parameters: _id, name, family, given, birthdate, identifier, gender, _lastUpdated.
/// </summary>
[Route("fhir/r4")]
public class PatientController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public PatientController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Patient/{id} — read a single Patient</summary>
    [HttpGet("Patient/{id}")]
    [ProducesResponseType(typeof(Patient), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var patient = await _adapter.GetPatientAsync(id, TenantId, ct);
        return patient is null ? FhirNotFound("Patient", id) : Ok(patient);
    }

    /// <summary>GET /fhir/r4/Patient — search Patients</summary>
    [HttpGet("Patient")]
    [ProducesResponseType(typeof(Bundle), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> Search([FromQuery] PatientSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _adapter.SearchPatientsAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Patient", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }
}
