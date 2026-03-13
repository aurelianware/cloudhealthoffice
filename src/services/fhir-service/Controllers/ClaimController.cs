using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Claim resource — read and search.
/// Supports search parameters: _id, patient, created, status, use.
/// </summary>
[Route("fhir/r4")]
public class ClaimController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public ClaimController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Claim/{id}</summary>
    [HttpGet("Claim/{id}")]
    [ProducesResponseType(typeof(Claim), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var claim = await _adapter.GetClaimAsync(id, TenantId, ct);
        return claim is null ? FhirNotFound("Claim", id) : Ok(claim);
    }

    /// <summary>GET /fhir/r4/Claim — search Claims</summary>
    [HttpGet("Claim")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search([FromQuery] ClaimSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _adapter.SearchClaimsAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Claim", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }
}
