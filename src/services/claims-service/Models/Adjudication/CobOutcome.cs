using CloudHealthOffice.CobEngine.Domain;

namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Capability 5.8 — structured outcome of the
/// <see cref="Services.Adjudication.Stages.CoordinationOfBenefitsStage"/>.
/// Lives on <see cref="Services.Adjudication.ClaimAdjudicationContext.CobResult"/>;
/// not persisted via PersistenceStage in Phase 1 (α posture mirrors 5.4 —
/// projection deferred to Phase 2 priorEob work, see Decision 4 in the 5.8
/// architecture doc).
///
/// <para>
/// Telemetry consumers (the <c>cho.claims.adjudication.cob.*</c> namespace
/// emitted by the stage) project from this record. Phase 2 priorEob work
/// will extend <see cref="ClaimsService.Models.AdjudicationResult"/> with
/// CHO-secondary persistence fields (CobReduction, SecondaryPlanPayment,
/// PrimaryPayerPayment); 5.8 does not.
/// </para>
/// </summary>
public sealed class CobOutcome
{
    /// <summary>The classification of CHO's role on this claim.</summary>
    public CobScenario Scenario { get; init; }

    /// <summary>Wire-shape <see cref="Resolution.CobEntry.PayerName"/> of
    /// the primary payer when CHO is not primary; <c>null</c> otherwise.</summary>
    public string? PrimaryPayerName { get; init; }

    /// <summary>Wire-shape <see cref="Resolution.CobEntry.PayerId"/> of
    /// the primary payer (Phase 1: actually populated from the policy
    /// number — see <see cref="Resolution.CobEntry"/> remarks).</summary>
    public string? PrimaryPayerId { get; init; }

    /// <summary>True when at least one primary-sequence COB entry has
    /// <see cref="Resolution.CobEntry.IsMedicare"/> = true. Drives the
    /// <c>cho.claims.adjudication.cob.medicare_primary</c> counter and
    /// distinguishes Medicare-primary vs commercial-primary scenarios for
    /// Phase 2 priority sizing.</summary>
    public bool IsMedicarePrimary { get; init; }

    /// <summary>Stable machine reason code for the scenario, set whenever
    /// CHO is not the primary payer or coverage state is unknown — i.e.
    /// for <see cref="CobScenario.ChoSecondaryDetected"/>,
    /// <see cref="CobScenario.ChoTertiaryDetected"/>, and
    /// <see cref="CobScenario.None"/>. The code is stable across modes —
    /// it stays set even when the tenant's <see cref="CobEnforcementMode"/>
    /// rendered the stage's outcome non-Pend (Deny mode → still set;
    /// SoftValidation mode → still set for telemetry continuity). Only
    /// <see cref="CobScenario.ChoPrimaryNoSecondary"/> and
    /// <see cref="CobScenario.ChoPrimaryWithSecondary"/> leave it
    /// <c>null</c>. Phase 1 values:
    /// <c>cob-secondary-not-supported-phase-1</c>,
    /// <c>cob-coverage-service-unavailable</c>.</summary>
    public string? PendReason { get; init; }

    /// <summary>The engine's <see cref="PayerOrderRule"/> when the stage
    /// invoked <see cref="CloudHealthOffice.CobEngine.Services.IPayerOrderService.DetermineOrder"/>
    /// for audit-trail enrichment; <c>null</c> when no engine call was
    /// made (e.g. CHO-primary scenarios skip it). For CHO-secondary
    /// commercial cases the engine defaults to
    /// <see cref="PayerOrderRule.ExplicitCoverageRecord"/> because the
    /// Phase 1 InsuredInfo has no birthday / employment data; for
    /// Medicare-primary cases the engine returns
    /// <see cref="PayerOrderRule.MedicareSecondaryPayer"/>.</summary>
    public PayerOrderRule? AppliedRule { get; init; }
}

/// <summary>
/// Classification of CHO's role on a claim relative to the member's other
/// coverage. Drives both <see cref="CobOutcome.PendReason"/> selection and
/// the <c>cho.claims.adjudication.cob.outcome</c> telemetry tag.
/// </summary>
public enum CobScenario
{
    /// <summary>No coverage data was available — the stage degraded.
    /// Coverage-service was unavailable / returned an unparseable
    /// response. <see cref="CobOutcome.PendReason"/> is
    /// <c>cob-coverage-service-unavailable</c>. Outcome by mode
    /// (Decision 7 — "unable to determine coverage state" isn't
    /// structurally a denial scenario):
    /// <see cref="CobEnforcementMode.PendForSecondary"/> → Pend;
    /// <see cref="CobEnforcementMode.Deny"/> → Pend (NOT Deny);
    /// <see cref="CobEnforcementMode.SoftValidation"/> → Pass (telemetry
    /// captures the degradation but no policy effect).</summary>
    None,

    /// <summary>Coverage-service confirmed CHO is the only coverage
    /// (empty list / 404). Stage returns Pass; downstream stages
    /// proceed with single-payer adjudication.</summary>
    ChoPrimaryNoSecondary,

    /// <summary>Other coverage exists but is sequenced after CHO
    /// (CoverageSequence "S" / "T" only). CHO is primary; stage
    /// returns Pass. Recorded for Phase 2 priorEob roadmap sizing.</summary>
    ChoPrimaryWithSecondary,

    /// <summary>Exactly one other coverage is sequenced "P" — CHO is the
    /// secondary payer. Phase 2 hook: stage produces Pend (default mode)
    /// with the structured reason
    /// <c>cob-secondary-not-supported-phase-1</c>.</summary>
    ChoSecondaryDetected,

    /// <summary>Multiple primary or primary+secondary other coverages —
    /// CHO is at position 3 or later. Phase 2 hook: same Pend semantic
    /// as <see cref="ChoSecondaryDetected"/>; tracked separately for
    /// Phase 2 priorEob complexity sizing.</summary>
    ChoTertiaryDetected,
}
