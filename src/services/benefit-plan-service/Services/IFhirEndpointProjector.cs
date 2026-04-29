using System.Text.Json.Nodes;
using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Projects a <see cref="PlanDocumentReference"/> attached to a published
/// <see cref="BenefitPlan"/> into a FHIR R4 Endpoint resource (capability
/// BP 5.9 — Plan Documents → FHIR Endpoint projection). Pattern mirrors
/// <see cref="IFhirInsurancePlanProjector"/>: hand-built to avoid the
/// Hl7.Fhir.R4 transitive dep graph and keep serialization deterministic
/// for tests.
///
/// <para>
/// One <c>Endpoint</c> resource is emitted per <c>PlanDocumentReference</c>
/// whose <c>Location</c> is an external HTTPS URL. Documents whose
/// <c>Location</c> is the reserved internal <c>documentreference/{id}</c>
/// form (Phase 2 forward-compat) are NOT projectable to an Endpoint —
/// Endpoints require an external address. The projector skips such
/// documents and the caller increments
/// <c>cho.benefit_plan.endpoint_skipped_internal_reference.total</c>.
/// </para>
///
/// <para>
/// The projection emits <c>meta.profile</c> with the Da Vinci Plan-Net
/// 1.1.0 Endpoint profile. <c>connectionType</c> carries a single CHO
/// CodeSystem coding (<c>static-document</c>, Decision 1) because the
/// HL7 <c>endpoint-connection-type</c> CodeSystem has no code for
/// "static downloadable document." <c>payloadType.coding</c> carries a
/// CHO CodeSystem coding mapped from <c>PlanDocumentType</c>
/// (Decision 3) because Plan-Net does not bind the slot.
/// </para>
///
/// <para>
/// <c>Endpoint.id</c> is <see cref="PlanDocumentReference.Id"/> verbatim
/// (Decision 2) — matches the BP 5.8 stance for
/// <c>InsurancePlan.id = BenefitPlan.PlanId</c> (use the source-system
/// identifier directly).
/// </para>
///
/// <para>
/// Hash exposure (<see cref="PlanDocumentReference.ContentHashSha256"/>)
/// is intentionally not projected here — Plan-Net's <c>Endpoint</c> profile
/// has no <c>Attachment</c>-shaped slot for it. Hash exposure waits for
/// the Phase 2 <c>DocumentReference</c> projection in
/// member-document-service.
/// </para>
/// </summary>
public interface IFhirEndpointProjector
{
    /// <summary>
    /// Project a single <see cref="PlanDocumentReference"/> into a FHIR
    /// <c>Endpoint</c> resource. Returns <c>null</c> when the document is
    /// not projectable — either the parent plan is non-Published, or the
    /// document's <c>Location</c> is the reserved internal
    /// <c>documentreference/{id}</c> form (skip-internal-reference per
    /// Decision 4).
    /// </summary>
    JsonObject? Project(BenefitPlan plan, PlanDocumentReference document);

    /// <summary>
    /// Project every projectable document on <paramref name="plan"/>.
    /// Documents are emitted in the order returned by
    /// <see cref="OrderedProjectableDocuments(BenefitPlan)"/> (Decision 8 —
    /// SBC before EOC before Formulary before SPD before MRF before Other;
    /// within DocType, EffectiveDate desc, then Id).
    /// Returns an empty array when the plan is not Published or has no
    /// projectable documents.
    /// </summary>
    JsonArray ProjectAll(BenefitPlan plan);

    /// <summary>
    /// Enumerate the plan's documents that <see cref="Project"/> would
    /// emit, in the canonical Decision 8 order. Used by
    /// <see cref="IFhirInsurancePlanProjector"/> to build
    /// <c>InsurancePlan.endpoint[]</c> as <c>Reference(Endpoint/{id})</c>
    /// without re-projecting the documents.
    /// </summary>
    IReadOnlyList<PlanDocumentReference> OrderedProjectableDocuments(BenefitPlan plan);
}
