using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Communication resource — appeal note projection.
/// Each AppealNote on an appeal is rendered as a Communication
/// conforming to the <c>cho-appeal-communication</c> profile, with
/// <c>Communication.about</c> referencing the parent Task. See
/// <see cref="FhirAppealMapper.ToAppealCommunications"/>.
/// </summary>
[Route("fhir/r4")]
public sealed class CommunicationController : FhirControllerBase
{
    private readonly IFhirAppealAdapter _appeals;
    private readonly FhirAppealMapper _mapper;
    private readonly FhirBundleBuilder _bundleBuilder;

    public CommunicationController(
        IFhirAppealAdapter appeals,
        FhirAppealMapper mapper,
        FhirBundleBuilder bundleBuilder)
    {
        _appeals = appeals;
        _mapper = mapper;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Communication/{id} — read a single note as Communication.</summary>
    [HttpGet("Communication/{id}")]
    [ProducesResponseType(typeof(Communication), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        // Notes are embedded in the appeal. Finding the parent appeal
        // requires scanning: current adapter shape doesn't support
        // per-note read directly, so we search and project. The search
        // endpoint scopes by tenant via the adapter's HttpClient.
        var (appeals, _) = await _appeals.SearchAppealsAsync(
            new AppealSearchQuery { PageSize = 100 }, TenantId, ct);

        foreach (var appeal in appeals)
        {
            var note = appeal.Notes.FirstOrDefault(n => n.NoteId == id);
            if (note is null) continue;
            var communication = _mapper.ToAppealCommunications(appeal)
                .FirstOrDefault(c => c.Id == id);
            if (communication is not null) return Ok(communication);
        }

        return FhirNotFound("Communication", id);
    }

    /// <summary>GET /fhir/r4/Communication — search notes across appeals.</summary>
    [HttpGet("Communication")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search(
        [FromQuery] AppealCommunicationSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var query = new AppealSearchQuery
        {
            MemberId = StripPrefix("Patient/", search.Patient)
                       ?? StripPrefix("Patient/", SmartPatientId),
            ClaimId = null,
            PageSize = search.Count
        };

        // If the caller filters by `about=Task/{id}`, that's a single
        // appeal's notes — translate and short-circuit.
        if (!string.IsNullOrEmpty(search.About))
        {
            var appealId = StripPrefix("Task/", search.About);
            if (!string.IsNullOrEmpty(appealId))
            {
                var appeal = await _appeals.GetAppealAsync(appealId, TenantId, ct);
                var notes = appeal is null
                    ? Array.Empty<Communication>()
                    : _mapper.ToAppealCommunications(appeal).ToArray();
                var singleBundle = _bundleBuilder.Build(
                    notes, notes.Length, search.Page, search.Count,
                    "Communication", FhirBaseUrl, RawQueryString);
                return Ok(singleBundle);
            }
        }

        var (appeals, _) = await _appeals.SearchAppealsAsync(query, TenantId, ct);
        var comms = appeals.SelectMany(_mapper.ToAppealCommunications).ToList();

        var bundle = _bundleBuilder.Build(
            comms, comms.Count, search.Page, search.Count,
            "Communication", FhirBaseUrl, RawQueryString);
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
