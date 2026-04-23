using CloudHealthOffice.Appeals.Contracts;
using FhirService.Models;

namespace FhirService.Services;

/// <summary>
/// Adapter over appeals-service for the FHIR read + submit surfaces.
/// Returns <see cref="AppealDto"/> (the cross-service contract) rather
/// than FHIR resources — mapping from AppealDto to Task /
/// Communication / DocumentReference / ClaimResponse is the
/// responsibility of <see cref="FhirAppealMapper"/>, keeping the
/// HTTP-plumbing seam and the FHIR-semantics seam cleanly separated.
///
/// Intentionally a separate interface from <see cref="IFhirDataAdapter"/>:
/// appeals are a bespoke CHO domain with a dedicated backing service,
/// and keeping the surfaces separate avoids the awkwardness of a
/// single adapter method that returns multiple FHIR resource types
/// projected from one domain record.
/// </summary>
public interface IFhirAppealAdapter
{
    /// <summary>
    /// Read a single appeal by id, tenant-scoped.
    /// Returns null when not found for this tenant (404 at controller).
    /// </summary>
    Task<AppealDto?> GetAppealAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Search appeals with tenant-scoped filters. The caller (controller
    /// per FHIR resource) is responsible for translating FHIR search
    /// parameters into <see cref="AppealSearchQuery"/> and then filtering
    /// the results to the projection the resource represents.
    /// </summary>
    Task<(IReadOnlyList<AppealDto> Items, int Total)> SearchAppealsAsync(
        AppealSearchQuery query, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Execute the <c>$cho-appeal-submit</c> operation's child calls
    /// against appeals-service. Returns one outcome per child, in the
    /// order they were attempted:
    ///  1. POST /api/appeals (always first, success is prerequisite
    ///     for notes/attachments).
    ///  2. One outcome per entry in <see cref="AppealSubmitBundleDto.Notes"/>.
    ///  3. One outcome per entry in <see cref="AppealSubmitBundleDto.Attachments"/>.
    /// </summary>
    Task<IReadOnlyList<AppealSubmitChildOutcome>> SubmitAppealAsync(
        AppealSubmitBundleDto bundle, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Read a single note by its noteId, returning both the note and the
    /// minimal parent appeal context needed to project a FHIR Communication.
    /// Returns null when not found or when the note belongs to a different tenant.
    /// </summary>
    Task<(AppealDto Appeal, AppealNoteDto Note)?> GetNoteByIdAsync(
        string noteId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Read a single attachment by its attachmentId, returning both the
    /// attachment and the minimal parent appeal context needed to project a
    /// FHIR DocumentReference.
    /// Returns null when not found or when the attachment belongs to a different tenant.
    /// </summary>
    Task<(AppealDto Appeal, AppealAttachmentDto Attachment)?> GetAttachmentByIdAsync(
        string attachmentId, string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Normalised view of the cross-resource appeal search parameters. FHIR
/// controllers translate their resource-specific search types into this
/// canonical shape before calling the adapter.
/// </summary>
public sealed record AppealSearchQuery
{
    /// <summary>Member id (maps to `patient` on all four FHIR surfaces).</summary>
    public string? MemberId { get; init; }

    /// <summary>Domain status filter (AppealStatus enum name, case-insensitive).</summary>
    public string? Status { get; init; }

    /// <summary>Original Claim id — used by Task.focus and ClaimResponse.request.</summary>
    public string? ClaimId { get; init; }

    /// <summary>Assigned reviewer id — used by Task.owner.</summary>
    public string? AssignedReviewerId { get; init; }

    /// <summary>
    /// Closure reason filter. Populated by FHIR status queries that need
    /// to distinguish Approved / Denied / PartialApproval / Withdrawn
    /// after the status-consolidation that PR 2 landed.
    /// </summary>
    public AppealClosureReasonCode? ClosureReasonCode { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
