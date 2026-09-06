using CloudHealthOffice.Appeals.Contracts;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Cdex;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using FhirTask = Hl7.Fhir.Model.Task;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Task resource.
///
/// TWO kinds of Task are served here, distinguished by <c>Task.code</c> and by
/// profile, as FHIR intends — one resource type, several profiles:
///
/// <list type="bullet">
/// <item>CHO appeals, on the <c>cho-appeal-task</c> profile. See
///   <see cref="FhirAppealMapper.ToAppealTask"/>.</item>
/// <item>Da Vinci CDex additional-information requests on a pended prior
///   authorization, on the CDex Task Attachment Request profile. See
///   <see cref="CdexTaskMapper"/>. This is the REQUEST half of the PAS-07 round
///   trip: how a provider retrieves what the payer needs from them. The response
///   half is <c>$submit-attachment</c> in <see cref="CdexController"/>.</item>
/// </list>
///
/// DISPATCH. A read goes to the CDex store when the id carries the reserved
/// <c>rfai-</c> prefix, which is the additional-information case's own document
/// id prefix — so no lookup has to be attempted against both stores. A search
/// goes to the CDex store when it asks for the CDex code, or names a tracking
/// id in <c>identifier</c>. Everything else is an appeal search, exactly as
/// before.
///
/// Both kinds are tenant-scoped from the authenticated context and both are
/// gated on a <c>Task</c> read scope by <c>SmartScopeEnforcementMiddleware</c>.
/// </summary>
[Route("fhir/r4")]
public sealed class TaskController : FhirControllerBase
{
    private readonly IFhirAppealAdapter _appeals;
    private readonly FhirAppealMapper _mapper;
    private readonly FhirBundleBuilder _bundleBuilder;
    private readonly ICdexAdditionalInformationStore _additionalInformation;
    private readonly CdexTaskMapper _cdexMapper;
    private readonly ILogger<TaskController> _logger;

    public TaskController(
        IFhirAppealAdapter appeals,
        FhirAppealMapper mapper,
        FhirBundleBuilder bundleBuilder,
        ICdexAdditionalInformationStore additionalInformation,
        CdexTaskMapper cdexMapper,
        ILogger<TaskController> logger)
    {
        _appeals = appeals;
        _mapper = mapper;
        _bundleBuilder = bundleBuilder;
        _additionalInformation = additionalInformation;
        _cdexMapper = cdexMapper;
        _logger = logger;
    }

    /// <summary>GET /fhir/r4/Task/{id} — read one Task.</summary>
    [HttpGet("Task/{id}")]
    [ProducesResponseType(typeof(FhirTask), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        if (CdexTaskMapper.IsAdditionalInformationTaskId(id))
            return await ReadAdditionalInformationAsync(id, ct);

        var appeal = await _appeals.GetAppealAsync(id, TenantId, ct);
        if (appeal is null) return FhirNotFound("Task", id);

        var task = _mapper.ToAppealTask(appeal);
        return Ok(task);
    }

    /// <summary>GET /fhir/r4/Task — search Tasks.</summary>
    [HttpGet("Task")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> Search(
        [FromQuery] AppealTaskSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        if (IsAdditionalInformationSearch(search))
            return await SearchAdditionalInformationAsync(search, ct);

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

    // ── CDex additional-information requests ────────────────────────────

    /// <summary>
    /// The CDex code that selects additional-information requests. Matched
    /// against a bare code and against the <c>system|code</c> token form, both
    /// of which are legal FHIR token search syntax.
    /// </summary>
    private static bool IsAdditionalInformationSearch(AppealTaskSearchParams search)
    {
        if (!string.IsNullOrWhiteSpace(search.Identifier)) return true;
        if (string.IsNullOrWhiteSpace(search.Code)) return false;

        var code = search.Code.Contains('|', StringComparison.Ordinal)
            ? search.Code[(search.Code.LastIndexOf('|') + 1)..]
            : search.Code;

        return string.Equals(
            code.Trim(), CdexCanonicalUrls.AttachmentRequestCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IActionResult> ReadAdditionalInformationAsync(string id, CancellationToken ct)
    {
        var request = await _additionalInformation.GetByIdAsync(TenantId, id, ct);

        // Unknown and other-tenant are the SAME answer. The store's lookup is
        // tenant-scoped and the record's own tenant is re-checked, so a request
        // belonging to another tenant is indistinguishable from one that does
        // not exist.
        if (request is null || !string.Equals(request.TenantId, TenantId, StringComparison.Ordinal))
            return FhirNotFound("Task", id);

        await RecordDeliveryAsync(request, ct);
        return Ok(_cdexMapper.ToAttachmentRequestTask(request));
    }

    private async Task<IActionResult> SearchAdditionalInformationAsync(
        AppealTaskSearchParams search, CancellationToken ct)
    {
        var matches = await FindAdditionalInformationAsync(search, ct);

        // status is applied to the PROJECTED Task.status, so a caller filters on
        // what they can see rather than on a CHO state name they cannot.
        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            matches = matches
                .Where(r => string.Equals(
                    CdexTaskMapper.MapStatus(r).Status.ToString(),
                    search.Status.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var total = matches.Count;
        var page = matches
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        foreach (var request in page)
            await RecordDeliveryAsync(request, ct);

        var tasks = page.Select(_cdexMapper.ToAttachmentRequestTask).ToList();

        var bundle = _bundleBuilder.Build(
            tasks, total, search.Page, search.Count,
            "Task", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }

    /// <summary>
    /// Resolves the search to concrete requests. Both supported keys are exact:
    /// the tracking id names one request, and <c>focus</c> names the prior
    /// authorization whose cycles are wanted. A bare <c>code</c> search with
    /// neither returns nothing rather than every outstanding request in the
    /// tenant — a documentation request is addressed to ONE provider, and this
    /// endpoint has no provider identity to filter by (see the caller-binding
    /// limitation on the submission service).
    /// </summary>
    private async Task<List<Services.Cdex.CdexAdditionalInformationRequest>>
        FindAdditionalInformationAsync(AppealTaskSearchParams search, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(search.Identifier))
        {
            var trackingId = TokenValue(search.Identifier);
            var byTracking = await _additionalInformation.GetByTrackingIdAsync(
                TenantId, trackingId, ct);

            return byTracking is not null
                   && string.Equals(byTracking.TenantId, TenantId, StringComparison.Ordinal)
                ? [byTracking]
                : [];
        }

        var authNumber = StripPrefix("Claim/", search.Focus);
        if (string.IsNullOrWhiteSpace(authNumber))
        {
            _logger.LogInformation(
                "CDex Task search without an identifier or focus returned nothing.");
            return [];
        }

        var cycles = await _additionalInformation.GetByAuthorizationNumberAsync(
            TenantId, authNumber, ct);

        return cycles
            .Where(r => string.Equals(r.TenantId, TenantId, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Stamps provenance: the payer's request has been handed to the requester.
    /// Best-effort by design — failing to record that a request was delivered
    /// must not stop a provider from learning what the payer needs.
    /// </summary>
    private async System.Threading.Tasks.Task RecordDeliveryAsync(
        Services.Cdex.CdexAdditionalInformationRequest request, CancellationToken ct)
    {
        if (!request.IsOpen) return;
        await _additionalInformation.MarkDeliveredAsync(TenantId, request.Id, ct);
    }

    /// <summary>Takes the code half of a <c>system|code</c> token search value.</summary>
    private static string TokenValue(string value)
        => value.Contains('|', StringComparison.Ordinal)
            ? value[(value.LastIndexOf('|') + 1)..].Trim()
            : value.Trim();

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
