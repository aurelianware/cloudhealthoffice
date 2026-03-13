using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 ExplanationOfBenefit resource — read and search.
/// Supports search parameters: _id, patient, created, type, status.
/// CMS-0057-F Patient Access API requires EOBs searchable by patient.
/// </summary>
[Route("fhir/r4")]
public class ExplanationOfBenefitController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public ExplanationOfBenefitController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/ExplanationOfBenefit/{id}</summary>
    [HttpGet("ExplanationOfBenefit/{id}")]
    [ProducesResponseType(typeof(ExplanationOfBenefit), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var eob = await _adapter.GetEobAsync(id, TenantId, ct);
        return eob is null ? FhirNotFound("ExplanationOfBenefit", id) : Ok(eob);
    }

    /// <summary>GET /fhir/r4/ExplanationOfBenefit — search EOBs</summary>
    [HttpGet("ExplanationOfBenefit")]
    [ProducesResponseType(typeof(Bundle), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> Search([FromQuery] EobSearchParams search, CancellationToken ct)
    {
        // CMS-0057-F requires patient parameter for Patient Access API calls
        if (string.IsNullOrEmpty(search.Patient) && string.IsNullOrEmpty(search.Id))
            return FhirBadRequest("ExplanationOfBenefit search requires 'patient' or '_id' parameter");

        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _adapter.SearchEobsAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "ExplanationOfBenefit", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }
}
