using CloudHealthOffice.Appeals.Contracts;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using FhirTask = Hl7.Fhir.Model.Task;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Task resource — appeal projection.
/// Each CHO appeal is rendered as a Task conforming to the
/// <c>cho-appeal-task</c> profile. See
/// <see cref="FhirAppealMapper.ToAppealTask"/> for the projection rules.
/// </summary>
[Route("fhir/r4")]
public sealed class TaskController : FhirControllerBase
{
    private readonly IFhirAppealAdapter _appeals;
    private readonly FhirAppealMapper _mapper;
    private readonly FhirBundleBuilder _bundleBuilder;

    public TaskController(
        IFhirAppealAdapter appeals,
        FhirAppealMapper mapper,
        FhirBundleBuilder bundleBuilder)
    {
        _appeals = appeals;
        _mapper = mapper;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Task/{id} — read a single appeal as Task.</summary>
    [HttpGet("Task/{id}")]
    [ProducesResponseType(typeof(FhirTask), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var appeal = await _appeals.GetAppealAsync(id, TenantId, ct);
        if (appeal is null) return FhirNotFound("Task", id);

        var task = _mapper.ToAppealTask(appeal);
        return Ok(task);
    }

    /// <summary>GET /fhir/r4/Task — search appeals as Tasks.</summary>
    [HttpGet("Task")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search(
        [FromQuery] AppealTaskSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var query = new AppealSearchQuery
        {
            MemberId = StripPrefix("Patient/", search.Patient)
                       ?? StripPrefix("Patient/", SmartPatientId),
            Status = MapTaskStatusToAppealStatus(search.Status),
            ClaimId = StripPrefix("Claim/", search.Focus),
            AssignedReviewerId = StripPrefix("Practitioner/", search.Owner),
            ClosureReasonCode = MapTaskStatusToClosureReason(search.Status),
            Page = search.Page,
            PageSize = search.Count
        };

        var (items, _) = await _appeals.SearchAppealsAsync(query, TenantId, ct);
        var tasks = items.Select(_mapper.ToAppealTask).ToList();

        var bundle = _bundleBuilder.Build(
            tasks, tasks.Count, search.Page, search.Count,
            "Task", FhirBaseUrl, RawQueryString);
        return Ok(bundle);
    }

    // ── FHIR status → domain status translation ─────────────────────────
    // FHIR Task.status values narrower than the cho-appeal-task-status
    // ValueSet are translated back to the appeals domain AppealStatus +
    // ClosureReasonCode so the query reaches appeals-service in its
    // native shape.

    internal static string? MapTaskStatusToAppealStatus(string? fhirStatus) =>
        fhirStatus?.ToLowerInvariant() switch
        {
            "draft" => AppealStatus.Draft.ToString(),
            "requested" => AppealStatus.Submitted.ToString(),
            "in-progress" => AppealStatus.InReview.ToString(),
            "on-hold" => AppealStatus.PendingInfo.ToString(),
            "completed" or "rejected" or "cancelled" => AppealStatus.Closed.ToString(),
            _ => null
        };

    internal static AppealClosureReasonCode? MapTaskStatusToClosureReason(string? fhirStatus) =>
        fhirStatus?.ToLowerInvariant() switch
        {
            // Multiple reason codes collapse into Completed / Cancelled,
            // so a search by Task.status=completed doesn't narrow further
            // by reason here — the caller can filter post-hoc if they need
            // Approved vs PartialApproval distinction. Searching by
            // "rejected" is unambiguous: Denied only.
            "rejected" => AppealClosureReasonCode.Denied,
            _ => null
        };

    private static string? StripPrefix(string prefix, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}
