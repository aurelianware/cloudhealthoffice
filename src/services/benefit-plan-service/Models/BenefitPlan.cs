using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BenefitPlanService.Models.Benefits;
using BenefitRulePredicate = CloudHealthOffice.BenefitEngine.Domain.BenefitRulePredicate;

namespace BenefitPlanService.Models;

/// <summary>
/// Represents a health insurance benefit plan
/// </summary>
public class BenefitPlan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Tenant ID for multi-tenant isolation (partition key)
    /// </summary>
    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("payer")]
    public string Payer { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("effectiveDate")]
    public DateTime EffectiveDate { get; set; }

    [JsonPropertyName("terminationDate")]
    public DateTime? TerminationDate { get; set; }

    [Required]
    [JsonPropertyName("planType")]
    public PlanType PlanType { get; set; }

    [JsonPropertyName("metalLevel")]
    public MetalLevel? MetalLevel { get; set; }

    /// <summary>
    /// Line of Business (Commercial, Medicare, Medicaid, Exchange)
    /// Determines regulatory requirements, benefit mandates, and network rules
    /// </summary>
    [Required]
    [JsonPropertyName("lineOfBusiness")]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    [JsonPropertyName("benefits")]
    public List<Benefit> Benefits { get; set; } = new();

    [JsonPropertyName("networkTiers")]
    public List<NetworkTier> NetworkTiers { get; set; } = new();

    [JsonPropertyName("costSharing")]
    public CostSharing CostSharing { get; set; } = new();

    /// <summary>
    /// Plan-level documents (SBC, EOC, Formulary, etc.).
    /// Inline for now; Phase 2 will migrate to member-document-service via
    /// FHIR DocumentReference (see PR #650 sibling work). Field shape is
    /// kept forward-compatible — the migration is then a data-copy.
    /// TODO(benefits-viewer-phase2): migrate to DocumentReference resources.
    /// </summary>
    [JsonPropertyName("documents")]
    public List<PlanDocumentReference> Documents { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdDate")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modifiedDate")]
    public DateTime? ModifiedDate { get; set; }

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Backward-compatible activation flag. Semantically equivalent to
    /// <c>VersionState == Published</c> for new code; kept on the wire so
    /// existing consumers (eligibility-service, claims-service) don't break.
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Plan-level family accumulator pooling model (5.7). Embedded =
    /// per-member individual + family pools tracked independently;
    /// Aggregate = single shared family pool with an ACA 45 CFR §156.130
    /// per-member cap (enforced at runtime once the plan is republished
    /// after capability 5.7).
    ///
    /// <para>
    /// Defaults to <see cref="FamilyAccumulatorModel.Embedded"/>, which
    /// matches the engine's pre-5.7 implicit default. Legacy plan
    /// documents missing this field hydrate as Embedded — see
    /// docs/architecture/family-accumulator-models.md.
    /// </para>
    ///
    /// <para>
    /// Version-identity-bearing. Changing the model on a Published plan
    /// requires a new version (the same as any other cost-sharing change).
    /// </para>
    /// </summary>
    [JsonPropertyName("familyAccumulatorModel")]
    public FamilyAccumulatorModel FamilyAccumulatorModel { get; set; } = FamilyAccumulatorModel.Embedded;

    // ---------------------------------------------------------------------
    // Version identity (5.1 — Plan Identity & Versioning)
    //
    // A plan is an append-only chain of immutable Published versions. Each
    // row in this collection is one version. The chain is keyed on
    // (TenantId, PlanId); identity within the chain is the ULID VersionId.
    //
    // Documents written before these fields existed hydrate with
    // VersionState = Published, VersionNumber = 1, VersionId = Id.
    // See docs/architecture/plan-versioning.md.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Stable per-version identifier (ULID, Crockford base-32). Set
    /// explicitly by the service layer when a draft or legacy v1 is
    /// created. Empty on the wire ⇒ legacy row (predates this feature)
    /// and is hydrated as Published v1 on read.
    /// </summary>
    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = string.Empty;

    /// <summary>
    /// 1-based monotonic sequence within <c>(TenantId, PlanId)</c>.
    /// Populated by the service when creating new versions; left at the
    /// default for legacy documents so hydration can fix it up on read.
    /// </summary>
    [JsonPropertyName("versionNumber")]
    public int VersionNumber { get; set; }

    /// <summary>
    /// Lifecycle state. Populated by the service when creating new
    /// versions; legacy documents missing this field deserialize to the
    /// default and are normalized to <see cref="PlanVersionState.Published"/>
    /// during hydration.
    /// </summary>
    [JsonPropertyName("versionState")]
    public PlanVersionState VersionState { get; set; }
    // Defaults to PlanVersionState.Draft (enum value 0) for newly created instances.
    // Legacy documents that predate this field also deserialize to this default,
    // but are normalized to PlanVersionState.Published by Hydrate() when VersionId
    // is empty — the two conditions (VersionId empty AND VersionState==Draft) together
    // identify a legacy row, not a real draft.

    /// <summary>
    /// <see cref="VersionId"/> of the version this draft amends, if any.
    /// Null for the genesis version.
    /// </summary>
    [JsonPropertyName("predecessorVersionId")]
    public string? PredecessorVersionId { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("publishedBy")]
    public string? PublishedBy { get; set; }

    [JsonPropertyName("supersededAt")]
    public DateTime? SupersededAt { get; set; }

    [JsonPropertyName("supersededByVersionId")]
    public string? SupersededByVersionId { get; set; }

    // ---------------------------------------------------------------------
    // Plan-Year Definition (5.3 — Plan-Year Definition Foundation)
    //
    // Optional. Plans created before this feature deserialize with a null
    // definition and an empty accumulator-target list, which the
    // PlanYearScheduler treats as opt-out (no events emitted).
    // EffectiveDate / TerminationDate above are preserved verbatim and
    // remain authoritative for plan activation. See
    // docs/architecture/plan-year-definition.md.
    // ---------------------------------------------------------------------

    [JsonPropertyName("planYearDefinition")]
    public PlanYearDefinition? PlanYearDefinition { get; set; }

    [JsonPropertyName("accumulatorTargets")]
    public List<AccumulatorTarget> AccumulatorTargets { get; set; } = new();
}

/// <summary>
/// Individual benefit within a plan.
///
/// <para>
/// Phase 1 / 5.4 — Declarative Benefit Model. This base type carries every
/// facet that applies to every benefit (cost-sharing, prior-auth flags,
/// visit limits, etc.). Type-specific facets live on the concrete subclasses
/// in <c>BenefitPlanService.Models.Benefits</c>: <see cref="MedicalBenefit"/>,
/// <see cref="DentalBenefit"/>, <see cref="PharmacyBenefit"/>,
/// <see cref="BehavioralHealthBenefit"/>, <see cref="VisionBenefit"/>,
/// <see cref="DMEBenefit"/>, <see cref="MaternityBenefit"/>,
/// <see cref="PreventiveBenefit"/>.
/// </para>
///
/// <para>
/// Wire format: a polymorphic discriminator <c>"benefitType"</c> selects the
/// concrete subclass during deserialization. Legacy rows persisted before
/// 5.4 carry no discriminator; they hydrate as <see cref="MedicalBenefit"/>
/// (the catch-all default) so existing data continues to work without a
/// migration. See <c>docs/architecture/declarative-benefit-model.md</c>.
/// </para>
///
/// <para>
/// Engine integration: <c>BenefitCalculationEngine</c> and the prior-auth
/// rule engine continue to read this base class verbatim (Strategy A).
/// Type-aware engine paths (e.g. preventive zero-cost-share, formulary
/// resolution) arrive in subsequent capability prompts.
/// </para>
/// </summary>
public class Benefit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Discriminator written to the wire so the polymorphic converter can
    /// reconstruct the correct subclass. Each concrete subclass overrides
    /// this; the base default is <c>"medical"</c> so legacy rows that lack
    /// the property hydrate as <see cref="MedicalBenefit"/>.
    /// </summary>
    [JsonPropertyName("benefitType")]
    public virtual string BenefitType => BenefitTypeDiscriminators.Medical;

    [Required]
    [JsonPropertyName("serviceCategory")]
    public string ServiceCategory { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether the service is covered by this plan. Explicit exclusions are
    /// authored as benefit categories with this flag set to false so they
    /// participate in the same category mapping and rule-selection path as
    /// covered services.
    /// </summary>
    [JsonPropertyName("isCovered")]
    public bool IsCovered { get; set; } = true;

    [JsonPropertyName("cptCodes")]
    public List<string> CptCodes { get; set; } = new();

    [JsonPropertyName("inNetworkCopay")]
    public decimal? InNetworkCopay { get; set; }

    [JsonPropertyName("outNetworkCopay")]
    public decimal? OutNetworkCopay { get; set; }

    [JsonPropertyName("inNetworkCoinsurance")]
    public decimal? InNetworkCoinsurance { get; set; } // e.g., 0.20 for 20%

    [JsonPropertyName("outNetworkCoinsurance")]
    public decimal? OutNetworkCoinsurance { get; set; }

    [JsonPropertyName("deductibleApplies")]
    public bool DeductibleApplies { get; set; } = true;

    /// <summary>
    /// Whether patient cost for this benefit accumulates toward the
    /// out-of-pocket maximum. Most benefits do; some (e.g. non-essential
    /// services, certain out-of-network services) don't.
    /// </summary>
    [JsonPropertyName("oopApplies")]
    public bool OopApplies { get; set; } = true;

    [JsonPropertyName("priorAuthRequired")]
    public bool PriorAuthRequired { get; set; } = false;

    [JsonPropertyName("copayAmount")]
    public decimal? CopayAmount { get; set; }

    [JsonPropertyName("coinsurancePercentage")]
    public decimal? CoinsurancePercentage { get; set; }

    [JsonPropertyName("requiresPriorAuth")]
    public bool RequiresPriorAuth { get; set; }

    [JsonPropertyName("visitLimit")]
    public int? VisitLimit { get; set; }

    [JsonPropertyName("visitLimitPeriod")]
    public string? VisitLimitPeriod { get; set; }

    [JsonPropertyName("limitations")]
    public string? Limitations { get; set; }

    [JsonPropertyName("annualMaximum")]
    public decimal? AnnualMaximum { get; set; }

    [JsonPropertyName("lifetimeMaximum")]
    public decimal? LifetimeMaximum { get; set; }

    /// <summary>
    /// Optional declarative gates that restrict when this benefit applies
    /// to a given member encounter (age range, gender, required diagnosis
    /// codes, related-encounter lookback). Predicates are evaluated by
    /// callers; <c>null</c> or empty means the benefit always applies.
    /// </summary>
    [JsonPropertyName("rules")]
    public List<BenefitRulePredicate>? Rules { get; set; }
}

/// <summary>
/// Plan-level network tier (capability 5.5 — NetworkTier as Reference to
/// Organization). A tier is the operator-facing label and ranking that
/// claim adjudication uses to bucket cost-sharing (e.g. "In-Network" tier
/// 1 vs "Out-of-Network" tier 2). After capability 5.5, the canonical
/// roster lives on the provider-service <c>Organization</c> entity
/// (capability 5.3) and is referenced here by
/// <see cref="NetworkId"/> rather than embedded as a static NPI list.
///
/// <para>
/// During the migration window the legacy <see cref="ProviderNpis"/>
/// field is preserved on the wire so existing documents continue to
/// hydrate without a backfill — but it is no longer consulted by any
/// production code path (verified by repo-wide audit during 5.5 plan
/// phase). The field is removed in a follow-up PR after telemetry
/// confirms zero remaining legacy-shape rows.
/// </para>
///
/// <para>
/// <see cref="NetworkId"/> is nullable on purpose. A null value is a
/// legacy-tier marker that drives the soft-validation counter
/// <c>cho.benefit_plan.network_tier_missing_networkid_writes.total</c>;
/// the follow-up hard-validation PR flips this to <c>[Required]</c>
/// once the counter reads zero across all tenants for a sustained
/// window. See
/// <c>docs/architecture/network-tier-organization-reference.md</c>.
/// </para>
/// </summary>
public class NetworkTier
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("tierName")]
    public string TierName { get; set; } = string.Empty; // "In-Network", "Out-of-Network", "Preferred", etc.

    [JsonPropertyName("tierLevel")]
    public int TierLevel { get; set; } // 1 = best, 2 = second, etc.

    /// <summary>
    /// Reference to the canonical <c>Organization.OrganizationId</c>
    /// (chain key) in provider-service. Resolves the tier's roster
    /// authoritatively at lookup time rather than from an embedded
    /// snapshot. Nullable during the 5.5 → hard-validation rollout;
    /// null produces a soft-validation warning + counter increment.
    /// </summary>
    [JsonPropertyName("networkId")]
    public string? NetworkId { get; set; }

    /// <summary>
    /// Legacy embedded roster snapshot. Preserved on the wire during
    /// the 5.5 migration window so existing plan documents continue to
    /// hydrate; not consulted by any production code path. Removed in
    /// a follow-up PR once telemetry confirms zero remaining
    /// legacy-shape rows. New code must use <see cref="NetworkId"/>
    /// and resolve membership via <c>IOrganizationLookupClient</c>.
    /// </summary>
    [Obsolete("Use NetworkId + IOrganizationLookupClient. See docs/architecture/network-tier-organization-reference.md. Field is preserved during the 5.5 migration window only.")]
    [JsonPropertyName("providerNpis")]
    public List<string> ProviderNpis { get; set; } = new();
}

/// <summary>
/// Cost sharing details (deductibles, out-of-pocket maximums)
/// </summary>
public class CostSharing
{
    /// <summary>
    /// Plan-level default coinsurance percentage used by administrative
    /// surfaces. Service-level benefit rules remain authoritative during
    /// adjudication.
    /// </summary>
    [JsonPropertyName("coinsurance")]
    public decimal Coinsurance { get; set; }

    /// <summary>
    /// Informational monthly member premium displayed by administrative
    /// surfaces. Premium billing remains the financial system of record.
    /// </summary>
    [JsonPropertyName("monthlyPremium")]
    public decimal MonthlyPremium { get; set; }

    [JsonPropertyName("individualDeductible")]
    public decimal IndividualDeductible { get; set; }

    [JsonPropertyName("familyDeductible")]
    public decimal FamilyDeductible { get; set; }

    [JsonPropertyName("individualOutOfPocketMax")]
    public decimal IndividualOutOfPocketMax { get; set; }

    [JsonPropertyName("familyOutOfPocketMax")]
    public decimal FamilyOutOfPocketMax { get; set; }

    [JsonPropertyName("inNetworkDeductible")]
    public decimal InNetworkDeductible { get; set; }

    [JsonPropertyName("outOfNetworkDeductible")]
    public decimal OutOfNetworkDeductible { get; set; }

    [JsonPropertyName("inNetworkOutOfPocketMax")]
    public decimal InNetworkOutOfPocketMax { get; set; }

    [JsonPropertyName("outOfNetworkOutOfPocketMax")]
    public decimal OutOfNetworkOutOfPocketMax { get; set; }

    [JsonPropertyName("outNetworkIndividualDeductible")]
    public decimal? OutNetworkIndividualDeductible { get; set; }

    [JsonPropertyName("outNetworkFamilyDeductible")]
    public decimal? OutNetworkFamilyDeductible { get; set; }

    [JsonPropertyName("outNetworkIndividualOutOfPocketMax")]
    public decimal? OutNetworkIndividualOutOfPocketMax { get; set; }

    [JsonPropertyName("outNetworkFamilyOutOfPocketMax")]
    public decimal? OutNetworkFamilyOutOfPocketMax { get; set; }
}

/// <summary>
/// Plan-level document (SBC, EOC, Formulary, ...).
///
/// Shape is deliberately forward-compatible with FHIR DocumentReference so
/// that the planned Phase 2 migration to member-document-service becomes a
/// data-copy rather than a model redesign. Fields map as follows:
///   Location         -> DocumentReference.content.attachment.url
///   ContentType      -> DocumentReference.content.attachment.contentType
///   Size             -> DocumentReference.content.attachment.size
///   ContentHashSha256-> DocumentReference.content.attachment.hash (base64 of sha256)
///   DocType          -> DocumentReference.type.coding
///   Version          -> DocumentReference.version
///   EffectiveDate    -> DocumentReference.date
///
/// TODO(benefits-viewer-phase2): replace inline list with references to
/// member-document-service once that lands.
/// </summary>
public class PlanDocumentReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("docType")]
    public PlanDocumentType DocType { get; set; }

    /// <summary>
    /// Resolves to the document. Today this is an external HTTPS URL.
    /// After Phase 2, this may also be an internal reference of the
    /// form "documentreference/{id}" resolved by member-document-service.
    /// Consumers must accept both forms.
    /// </summary>
    [Required]
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>
    /// Base64-encoded SHA-256 of the document contents. Matches FHIR
    /// <c>DocumentReference.content.attachment.hash</c> exactly so the
    /// Phase 2 migration is a data-copy.
    ///
    /// Optional today; populate when available. Validated at producer
    /// boundaries (see <c>PlanDocumentValidation.ValidateHash</c>) — the
    /// setter itself is intentionally unvalidated so Mongo hydration and
    /// JSON deserialization of historical documents never throws here.
    /// </summary>
    [JsonPropertyName("contentHashSha256")]
    public string? ContentHashSha256 { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Plan document type discriminator.
///
/// <para>
/// Numeric values are explicit and APPEND-ONLY. The Mongo backend
/// (<c>BenefitPlanRepositoryMongo</c>) serializes enums as Int32 by
/// default — inserting a new value mid-list would shift the integer
/// codes of every value after it and silently corrupt every persisted
/// <c>docType</c> field. New entries must be appended after
/// <c>Other</c> with the next unused integer.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanDocumentType
{
    /// <summary>Summary of Benefits and Coverage (ACA mandated).</summary>
    SBC = 0,
    /// <summary>Evidence of Coverage / Certificate of Coverage.</summary>
    EOC = 1,
    /// <summary>Drug formulary.</summary>
    Formulary = 2,
    /// <summary>Summary Plan Description (ERISA).</summary>
    SPD = 3,
    /// <summary>Catch-all for plan documents that don't fit the named types.</summary>
    Other = 4,
    /// <summary>
    /// Machine-Readable Rate File (CMS Transparency in Coverage,
    /// 45 CFR §147.211). Promoted to a first-class type in BP 5.9 so the
    /// FHIR Endpoint projection is lossless; pre-BP-5.9 plan authors who
    /// stored MRFs under <see cref="Other"/> continue to round-trip
    /// without migration. Appended after <c>Other</c> with explicit value
    /// 5 so existing persisted Mongo Int32 codes for <c>Other</c> keep
    /// their meaning.
    /// </summary>
    MachineReadableRateFile = 5,
}

/// <summary>
/// Plan types
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanType
{
    HMO,
    PPO,
    EPO,
    POS,
    HDHP,
    Medicaid,
    Medicare,
    Commercial
}

/// <summary>
/// ACA metal levels
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetalLevel
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Catastrophic
}

/// <summary>
/// Line of Business - determines regulatory requirements and benefit rules
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineOfBusiness
{
    /// <summary>
    /// Commercial employer-sponsored coverage (ERISA regulated)
    /// </summary>
    Commercial = 1,

    /// <summary>
    /// Medicare Advantage (Part C) or Medicare Supplement
    /// CMS regulated, must follow Medicare coverage rules
    /// </summary>
    Medicare = 2,

    /// <summary>
    /// Medicaid Managed Care (state + federal regulated)
    /// EPSDT requirements, different benefit mandates per state
    /// </summary>
    Medicaid = 3,

    /// <summary>
    /// ACA Exchange/Marketplace individual plans
    /// QHP certification, Essential Health Benefits, metal levels required
    /// </summary>
    Exchange = 4,

    /// <summary>
    /// TRICARE (military health coverage)
    /// </summary>
    TRICARE = 5,

    /// <summary>
    /// Veterans Affairs health coverage
    /// </summary>
    VA = 6
}

/// <summary>
/// Plan-level family accumulator pooling model (capability 5.7). Mirrors
/// <see cref="CloudHealthOffice.BenefitEngine.Domain.FamilyAccumulatorModel"/>
/// in the engine; the service-side mirror exists so the persisted plan
/// document never takes a runtime dependency on the engine's domain
/// namespace, matching the boundary stance taken for <see cref="PlanType"/>.
///
/// <para>
/// <see cref="ChoBenefitPlanProvider"/> projects this value onto the
/// engine's <c>BenefitPlanConfig.FamilyAccumulatorModel</c> at adjudication
/// time; ACA-cap enforcement on Aggregate plans is gated by
/// <c>BenefitPlanConfig.IsAcaCapEnforced</c> (set true on republish after
/// capability 5.7, false on legacy hydration).
/// </para>
/// </summary>
public enum FamilyAccumulatorModel
{
    /// <summary>
    /// Each member has individual deductible / OOP; family aggregate also
    /// tracked. Individual met → that member's portion satisfied. Family
    /// met → all members' portions satisfied. Default for legacy plans.
    /// </summary>
    Embedded = 1,

    /// <summary>
    /// One shared family pool. Plus an ACA 45 CFR §156.130 per-member
    /// cap (enforced at runtime once <c>IsAcaCapEnforced</c> is true on
    /// the engine config). Common in HDHP / HSA plans.
    /// </summary>
    Aggregate = 2
}
