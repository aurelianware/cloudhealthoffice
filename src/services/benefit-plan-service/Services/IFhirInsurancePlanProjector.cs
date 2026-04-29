using System.Text.Json.Nodes;
using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Projects a <see cref="BenefitPlan"/> into a FHIR R4 InsurancePlan
/// resource (as a <see cref="JsonObject"/>) per the Plan-Net IG 1.1.0
/// + US Core 6.1.0 InsurancePlan profiles. Pattern mirrors
/// provider-service's <c>FhirPractitionerProjector</c>: hand-built to
/// avoid the Hl7.Fhir.R4 transitive dep graph and keep serialization
/// deterministic for tests.
///
/// <para>
/// Returns <c>null</c> when called on a non-Active plan version
/// (<c>VersionState != Published</c>) or when the effective window
/// excludes "now" (terminated plans). Caller maps null to FHIR
/// <c>OperationOutcome</c> 404. The "active" determination follows
/// the empirical Provider 5.7 convention — a non-Active version of a
/// resource has no public FHIR projection.
/// </para>
///
/// <para>
/// The projection emits <c>meta.profile</c> with both the US Core 6.1.0
/// InsurancePlan profile and the Da Vinci Plan-Net 1.1.0 InsurancePlan
/// profile. Required US Core elements (<c>identifier</c>, <c>status</c>,
/// <c>name</c>, <c>type</c>) are honored. Plan-Net <c>network</c>,
/// <c>coverage.benefit</c>, <c>plan.generalCost</c>, and
/// <c>plan.specificCost</c> are emitted from the plan's NetworkTiers,
/// Benefits, and CostSharing respectively.
/// </para>
///
/// <para>
/// Plan-Net <c>InsurancePlan.endpoint</c> (payer-published URLs for SBC,
/// formulary, machine-readable rate file) is populated by capability
/// BP 5.9 via the Plan Documents → FHIR Endpoint projection. Each
/// projectable <see cref="PlanDocumentReference"/> on the plan emits one
/// <c>Reference(Endpoint/{id})</c>; the Endpoint resources themselves
/// are dereferenceable at <c>/fhir/r4/Endpoint/{id}</c>. Documents whose
/// <c>Location</c> is the reserved internal <c>documentreference/{id}</c>
/// form are skipped (Phase 2 forward-compat). See
/// <c>docs/architecture/fhir-endpoint-projection.md</c>. Plan-Net
/// <c>coverageArea</c>, <c>contact</c>, <c>alias</c>, and
/// <c>administeredBy</c> remain deferred to Phase 2 (CHO has no source
/// data for them today).
/// </para>
///
/// <para>
/// The <c>InsurancePlan.ownedBy</c> reference is emitted as
/// <c>display</c>-only carrying <see cref="BenefitPlan.Payer"/>
/// (Decision 12) — the Payer field is a free-text string, not a
/// reference to a payer-Organization. A future "Payer Organization
/// Linking" capability migrates to a Reference shape.
/// </para>
/// </summary>
public interface IFhirInsurancePlanProjector
{
    /// <summary>
    /// Project a benefit plan without optional enrichments. Equivalent
    /// to <see cref="Project(BenefitPlan, IReadOnlyList{OrganizationLookupResult}?, AcaLimits?)"/>
    /// with <c>networks = null</c> and <c>acaLimits = null</c>.
    /// </summary>
    JsonObject? Project(BenefitPlan plan);

    /// <summary>
    /// Project a benefit plan with optional network enrichment and ACA
    /// limit lookup.
    ///
    /// <para>
    /// When <paramref name="networks"/> is non-null and contains an
    /// entry whose <c>OrganizationId</c> matches a NetworkTier's
    /// <c>NetworkId</c>, the projector adds <c>display</c> text to the
    /// emitted <c>Reference</c> from
    /// <see cref="OrganizationLookupResult.Name"/> so consumers see a
    /// human-readable network name without dereferencing. When null or
    /// empty, references emit without <c>display</c> text.
    /// </para>
    ///
    /// <para>
    /// When <paramref name="acaLimits"/> is non-null AND the plan is
    /// Aggregate-mode AND <see cref="AcaCapEnforcementPolicy.IsEnforced"/>
    /// returns true, an additional <c>plan.generalCost</c> entry surfaces
    /// the ACA per-member individual cap (Decision 11 dual emission). The
    /// top-level <c>insuranceplan-aca-cap-enforced</c> CHO extension is
    /// emitted regardless of <paramref name="acaLimits"/> so consumers
    /// always see the enforcement state; the per-cost entry is what
    /// requires the actual cap value.
    /// </para>
    /// </summary>
    JsonObject? Project(
        BenefitPlan plan,
        IReadOnlyList<OrganizationLookupResult>? networks,
        AcaLimits? acaLimits);
}
