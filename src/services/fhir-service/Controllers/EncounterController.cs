using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Encounter resource — read and search.
/// Supports search parameters: _id, patient, date, status, type.
/// </summary>
[Route("fhir/r4")]
public class EncounterController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public EncounterController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Encounter/{id}</summary>
    [HttpGet("Encounter/{id}")]
    [ProducesResponseType(typeof(Encounter), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var encounter = await _adapter.GetEncounterAsync(id, TenantId, ct);
        return encounter is null ? FhirNotFound("Encounter", id) : Ok(encounter);
    }

    /// <summary>GET /fhir/r4/Encounter — search Encounters</summary>
    [HttpGet("Encounter")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search([FromQuery] EncounterSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _adapter.SearchEncountersAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Encounter", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }
}
