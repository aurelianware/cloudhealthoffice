using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Coverage resource — read and search.
/// Supports search parameters: _id, patient, beneficiary, status, type.
/// </summary>
[Route("fhir/r4")]
public class CoverageController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public CoverageController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Coverage/{id} — read a single Coverage</summary>
    [HttpGet("Coverage/{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var coverage = await _adapter.GetCoverageAsync(id, TenantId, ct);
        return coverage is null ? FhirNotFound("Coverage", id) : Ok(coverage);
    }

    /// <summary>GET /fhir/r4/Coverage — search Coverage resources</summary>
    [HttpGet("Coverage")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search([FromQuery] CoverageSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _adapter.SearchCoverageAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Coverage", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }
}
