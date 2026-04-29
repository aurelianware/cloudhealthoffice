namespace BenefitPlanService.Services;

/// <summary>
/// Canonical URLs for FHIR artifacts the benefit-plan-service projector
/// emits (capability BP 5.8 — FHIR InsurancePlan Projection). Mirrors the
/// convention in provider-service's <c>ChoProviderFhirUrls</c> and
/// fhir-service's <c>ChoFhirCanonicalUrls</c> (base
/// <c>http://fhir.cloudhealthoffice.com/</c>).
///
/// <para>
/// CHO custom-extension URIs follow the empirical Provider 5.7/5.8/5.9
/// convention <c>{resource-lowercase}-{slug}</c> (no <c>cho-</c> prefix);
/// the Appeals-domain <c>cho-appeal-*</c> shape is appeals-specific and
/// does not propagate to other domains.
/// </para>
///
/// <para>
/// TODO: consolidate with fhir-service ChoFhirCanonicalUrls and
/// provider-service ChoProviderFhirUrls when a shared FHIR-infrastructure
/// project lands (Phase 2 cleanup PR). benefit-plan-service does not
/// reference fhir-service today, so the constants are mirrored here.
/// </para>
/// </summary>
internal static class ChoBenefitPlanFhirUrls
{
    public const string Base                    = "http://fhir.cloudhealthoffice.com/";
    public const string StructureDefinitionBase = Base + "StructureDefinition/";
    public const string CodeSystemBase          = Base + "CodeSystem/";

    // ── Standard FHIR / IG profile URLs ─────────────────────────────────

    public const string UsCoreInsurancePlanProfile =
        "http://hl7.org/fhir/us/core/StructureDefinition/us-core-insuranceplan";

    public const string PlanNetInsurancePlanProfile =
        "http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-InsurancePlan";

    /// <summary>
    /// HL7 R4 InsurancePlan.type CodeSystem. Plan-Net IG 1.1.0 binds
    /// <c>type.coding</c> to a value set whose codes include
    /// <c>medical</c>, <c>dental</c>, <c>vision</c>, <c>drug</c>. Phase 1
    /// emits <c>medical</c> as the default — every BenefitPlan today is
    /// medical-coverage-shaped. A future capability discriminates per
    /// authored plan type.
    /// </summary>
    public const string InsurancePlanTypeSystem =
        "http://terminology.hl7.org/CodeSystem/insurance-plan-type";

    /// <summary>
    /// CHO-canonical CodeSystem for the operator-authored product shape
    /// (<c>HMO</c> / <c>PPO</c> / <c>EPO</c> / <c>POS</c> / <c>HDHP</c> /
    /// <c>Medicaid</c> / <c>Medicare</c> / <c>Commercial</c>). Emitted as
    /// a second coding under <c>InsurancePlan.type</c> alongside the
    /// standard <see cref="InsurancePlanTypeSystem"/> — Decision 8a.
    /// </summary>
    public const string PlanProductShapeSystem =
        CodeSystemBase + "plan-product-shape";

    /// <summary>
    /// CHO-canonical CodeSystem for plan-level cost categories used in
    /// <c>plan.generalCost.type</c> (e.g. <c>deductible</c>,
    /// <c>out-of-pocket-max</c>, <c>aca-individual-cap</c>). FHIR R4
    /// has no canonical binding for this slot; CHO publishes one so
    /// consumers see a stable system+code pair instead of text-only
    /// concepts.
    /// </summary>
    public const string PlanGeneralCostTypeSystem =
        CodeSystemBase + "insuranceplan-general-cost-type";

    /// <summary>
    /// CHO-canonical CodeSystem for the <c>cost.qualifiers</c> codes
    /// (<c>in-network</c> / <c>out-of-network</c> / <c>copay</c> /
    /// <c>coinsurance</c>) emitted under
    /// <c>plan.specificCost.benefit.cost.qualifiers</c>. Plan-Net IG 1.1.0
    /// expects qualifier codes; the IG does not publish a canonical
    /// CodeSystem so CHO publishes one to keep the codes stable.
    /// </summary>
    public const string PlanCostQualifierSystem =
        CodeSystemBase + "insuranceplan-cost-qualifier";

    /// <summary>
    /// CHO-canonical CodeSystem for the operator-authored network tier
    /// identifier emitted under <c>plan.identifier</c> (so a Plan-Net
    /// consumer can look up "Tier 1" / "Tier 2" / "Out-of-Network" by
    /// the tier name the plan author wrote). CHO does not bind these
    /// to an external value set; plan authors author free-text tier
    /// names and we publish them as identifiers under a CHO base.
    /// </summary>
    public const string NetworkTierSystem =
        CodeSystemBase + "network-tier";

    /// <summary>
    /// Identifier system used for <c>InsurancePlan.identifier[0]</c>.
    /// Carries <c>BenefitPlan.PlanId</c> (operator-authored) per
    /// Decision 6 — the human-meaningful identifier consumers see on
    /// member ID cards and SBC documents. Tenant scoping disambiguates
    /// when two tenants happen to use the same value.
    /// </summary>
    public const string PlanIdSystem =
        Base + "plan-id";

    // ── CHO custom extensions for benefit-plan-specific data ─────────────

    /// <summary>
    /// CHO extension carrying <see cref="Models.FamilyAccumulatorModel"/>
    /// (capability BP 5.7). Emitted on <c>InsurancePlan</c> as
    /// <c>valueCode</c> (<c>Embedded</c> | <c>Aggregate</c>) so consumers
    /// can render the model choice without walking <c>generalCost</c> /
    /// <c>specificCost</c> heuristics. Decision 13.
    /// </summary>
    public const string FamilyAccumulatorModelExt =
        StructureDefinitionBase + "insuranceplan-family-accumulator-model";

    /// <summary>
    /// CHO extension carrying the resolved
    /// <see cref="AcaCapEnforcementPolicy.IsEnforced"/> flag. Emitted on
    /// <c>InsurancePlan</c> as <c>valueBoolean</c>; only present when
    /// enforcement is active for the plan. Plan-Net IG 1.1.0 has no
    /// native extension for "ACA per-member cap enforcement state"; CHO
    /// publishes one. Decision 13.
    ///
    /// <para>
    /// Also emitted as a sub-extension on the per-member ACA-cap
    /// <c>plan.generalCost</c> entry (Decision 11 dual emission) so
    /// standard Plan-Net consumers see the cap as a real cost limit while
    /// CHO-aware consumers can disambiguate it from a real plan-level
    /// individual cap.
    /// </para>
    /// </summary>
    public const string AcaCapEnforcedExt =
        StructureDefinitionBase + "insuranceplan-aca-cap-enforced";

    // ── BP 5.9 Endpoint projection ───────────────────────────────────────

    /// <summary>
    /// Plan-Net IG 1.1.0 Endpoint profile. Capability BP 5.9 — every
    /// projected <c>Endpoint</c> resource carries this profile in its
    /// <c>meta.profile</c> alongside the FHIR R4 base.
    /// </summary>
    public const string PlanNetEndpointProfile =
        "http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Endpoint";

    /// <summary>
    /// CHO-canonical CodeSystem for <c>Endpoint.connectionType</c>
    /// (Decision 1 in BP 5.9). The HL7
    /// <c>http://terminology.hl7.org/CodeSystem/endpoint-connection-type</c>
    /// CodeSystem does not have a code for "static downloadable document"
    /// — its codes are FHIR-protocol-shaped (<c>hl7-fhir-rest</c>,
    /// <c>direct-project</c>, <c>dicom-*</c>). CHO publishes one code,
    /// <see cref="EndpointConnectionTypeStaticDocument"/>, under this
    /// system. A future capability swaps in a standard code if HL7
    /// publishes one.
    /// </summary>
    public const string EndpointConnectionTypeSystem =
        CodeSystemBase + "endpoint-connection-type";

    /// <summary>
    /// The single CHO-published code under
    /// <see cref="EndpointConnectionTypeSystem"/>: the endpoint serves a
    /// downloadable static document (PDF, JSON, etc.).
    /// </summary>
    public const string EndpointConnectionTypeStaticDocument = "static-document";

    /// <summary>
    /// CHO-canonical CodeSystem for <c>Endpoint.payloadType.coding</c>
    /// (Decision 3 in BP 5.9). One code per
    /// <see cref="Models.PlanDocumentType"/> enum value
    /// (<c>sbc</c>, <c>eoc</c>, <c>formulary</c>, <c>spd</c>, <c>mrf</c>,
    /// <c>other</c>). Plan-Net IG 1.1.0 does not bind <c>payloadType</c>;
    /// no standard FHIR CodeSystem covers SBC / EOC / SPD / MRF.
    /// </summary>
    public const string PlanDocumentTypeSystem =
        CodeSystemBase + "plan-document-type";
}
