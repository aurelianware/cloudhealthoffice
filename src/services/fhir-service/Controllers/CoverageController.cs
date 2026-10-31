using FhirService.Services.Identity;
using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ChoNative;
using FhirService.Services.PlanOrganization;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Coverage resource — read and search.
/// Supports search parameters: _id, patient, beneficiary, status, type.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: "coverage:read")]
public class CoverageController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;
    private readonly PlanOrganizationOptions? _planOrganization;

    public CoverageController(
        IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder,
        IOptions<PlanOrganizationOptions>? planOrganization = null)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
        _planOrganization = planOrganization?.Value;
    }

    /// <summary>GET /fhir/r4/Coverage/{id} — read a single Coverage</summary>
    [HttpGet("Coverage/{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var coverage = await _adapter.GetCoverageAsync(id, TenantId, ct);
        return coverage is null || IsOutsidePatientContext(coverage.Beneficiary?.Reference)
            ? FhirNotFound("Coverage", id)
            : Ok(coverage);
    }

    /// <summary>GET /fhir/r4/Coverage — search Coverage resources</summary>
    [HttpGet("Coverage")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search([FromQuery] CoverageSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        // A patient-bound token sees its own coverage only. The middleware has
        // already refused an explicit patient/beneficiary naming someone else.
        if (AuthorizedMemberId is { } bound) search.Patient = bound;

        var (items, total) = await _adapter.SearchCoverageAsync(search, TenantId, ct);
        if (ResultsLeaveConfinement("Coverage", items.Select(c => c.Beneficiary?.Reference)))
            return FhirBadGateway("Coverage search could not be confined to the authorized member.");

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Coverage", FhirBaseUrl, RawQueryString);
        IncludePlanOrganization(bundle, search, items);

        return Ok(bundle);
    }

    /// <summary>
    /// <c>_include=Coverage:payor</c>: the one payor this service can include
    /// is the plan's own Organization, served from configuration. The adapter
    /// has already refused a search whose payors reference anything else.
    /// </summary>
    private void IncludePlanOrganization(Bundle bundle, CoverageSearchParams search, IReadOnlyList<Coverage> items)
    {
        var includesPayor = search.Include
            .SelectMany(v => v.Split(','))
            .Any(v => string.Equals(v.Trim(), ChoNativeFhirDataAdapter.CoveragePayorInclude, StringComparison.Ordinal));
        if (!includesPayor || _planOrganization is not { IsConfigured: true } plan) return;
        if (!items.Any(c => c.Payor.Any(p => PlanOrganizationResource.IsReferenceTo(plan, p.Reference)))) return;

        var organization = new FhirJsonParser().Parse<Organization>(PlanOrganizationResource.Project(plan).ToJsonString());
        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"{FhirBaseUrl}/Organization/{organization.Id}",
            Resource = organization,
            Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Include },
        });
    }
}
