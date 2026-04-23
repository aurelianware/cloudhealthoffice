using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 DocumentReference resource — appeal attachment /
/// clinical-document projection. Each attachment on an appeal is
/// rendered as a DocumentReference conforming to the
/// <c>cho-appeal-document-reference</c> profile, with
/// <c>Context.related</c> back-referencing the Task and 275 X12
/// extensions carrying transmission + control-number metadata.
/// </summary>
[Route("fhir/r4")]
public sealed class DocumentReferenceController : FhirControllerBase
{
    private readonly IFhirAppealAdapter _appeals;
    private readonly FhirAppealMapper _mapper;
    private readonly FhirBundleBuilder _bundleBuilder;

    public DocumentReferenceController(
        IFhirAppealAdapter appeals,
        FhirAppealMapper mapper,
        FhirBundleBuilder bundleBuilder)
    {
        _appeals = appeals;
        _mapper = mapper;
        _bundleBuilder = bundleBuilder;
    }

    [HttpGet("DocumentReference/{id}")]
    [ProducesResponseType(typeof(DocumentReference), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        // Fix 18: use dedicated GetAttachmentByIdAsync instead of scanning all appeals
        var result = await _appeals.GetAttachmentByIdAsync(id, TenantId, ct);
        if (result is null) return FhirNotFound("DocumentReference", id);
        var (appeal, attachment) = result.Value;
        var docRef = _mapper.ToAppealDocumentReference(attachment, appeal.Id, appeal.MemberId);
        return Ok(docRef);
    }

    [HttpGet("DocumentReference")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search(
        [FromQuery] AppealDocumentReferenceSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        // `related=Task/{id}` is the narrow-case: all docs for one appeal.
        if (!string.IsNullOrEmpty(search.Related))
        {
            var appealId = StripPrefix("Task/", search.Related);
            if (!string.IsNullOrEmpty(appealId))
            {
                var appeal = await _appeals.GetAppealAsync(appealId, TenantId, ct);
                var docs = appeal is null
                    ? Array.Empty<DocumentReference>()
                    : _mapper.ToAppealDocumentReferences(appeal).ToArray();
                var narrow = _bundleBuilder.Build(
                    docs, docs.Length, search.Page, search.Count,
                    "DocumentReference", FhirBaseUrl, RawQueryString);
                return Ok(narrow);
            }
        }

        var query = new AppealSearchQuery
        {
            MemberId = StripPrefix("Patient/", search.Patient)
                       ?? StripPrefix("Patient/", SmartPatientId),
            Page = search.Page,
            // Fix 6: use larger page to fetch more appeals for projection-level pagination
            PageSize = 500
        };
        var (appeals, _) = await _appeals.SearchAppealsAsync(query, TenantId, ct);
        // Fix 6: project all document references first, then paginate at this level
        var allDocs = appeals.SelectMany(_mapper.ToAppealDocumentReferences).ToList();
        var pagedDocs = allDocs
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        var bundle = _bundleBuilder.Build(
            pagedDocs, allDocs.Count, search.Page, search.Count,
            "DocumentReference", FhirBaseUrl, RawQueryString);
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
