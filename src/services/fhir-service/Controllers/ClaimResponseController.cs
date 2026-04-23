using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 ClaimResponse resource — appeal-decision projection.
/// A ClaimResponse exists only for Closed appeals with a decision;
/// Draft / Submitted / InReview / PendingInfo appeals and Withdrawn /
/// Expired / AdminError closures do NOT produce a ClaimResponse (their
/// Task is still readable).
///
/// The ClaimResponse is a back-reference via the
/// <c>cho-appeal-task-reference</c> extension to the work item (Task)
/// that produced it.
/// </summary>
[Route("fhir/r4")]
public sealed class ClaimResponseController : FhirControllerBase
{
    private readonly IFhirAppealAdapter _appeals;
    private readonly FhirAppealMapper _mapper;
    private readonly FhirBundleBuilder _bundleBuilder;

    public ClaimResponseController(
        IFhirAppealAdapter appeals,
        FhirAppealMapper mapper,
        FhirBundleBuilder bundleBuilder)
    {
        _appeals = appeals;
        _mapper = mapper;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/ClaimResponse/{id} — id is "{appealId}-response".</summary>
    [HttpGet("ClaimResponse/{id}")]
    [ProducesResponseType(typeof(ClaimResponse), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        // ClaimResponse ID shape: "{appealId}-response". Strip the suffix
        // to find the parent appeal.
        if (!id.EndsWith("-response", StringComparison.Ordinal))
            return FhirNotFound("ClaimResponse", id);

        var appealId = id[..^"-response".Length];
        var appeal = await _appeals.GetAppealAsync(appealId, TenantId, ct);
        if (appeal is null) return FhirNotFound("ClaimResponse", id);

        var claimResponse = _mapper.ToAppealClaimResponse(appeal);
        return claimResponse is null
            ? FhirNotFound("ClaimResponse", id)
            : Ok(claimResponse);
    }

    [HttpGet("ClaimResponse")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search(
        [FromQuery] AppealClaimResponseSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var query = new AppealSearchQuery
        {
            MemberId = StripPrefix("Patient/", search.Patient)
                       ?? StripPrefix("Patient/", SmartPatientId),
            ClaimId = StripPrefix("Claim/", search.Request),
            Page = search.Page,
            PageSize = search.Count
        };

        var (appeals, _) = await _appeals.SearchAppealsAsync(query, TenantId, ct);
        var responses = appeals
            .Select(_mapper.ToAppealClaimResponse)
            .Where(c => c is not null)
            .Cast<ClaimResponse>()
            .ToList();

        var bundle = _bundleBuilder.Build(
            responses, responses.Count, search.Page, search.Count,
            "ClaimResponse", FhirBaseUrl, RawQueryString);
        return Ok(bundle);
    }

    private static string? StripPrefix(string prefix, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}
