namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Enumerates all edge case scenarios in the Million Claim Challenge benchmark corpus.
/// Each scenario tests a specific adjudication pathway that is commonly mis-handled.
/// </summary>
public enum EdgeCaseScenario
{
    // COB Scenarios (12,000 claims)

    /// <summary>Claim where this payer is primary.</summary>
    CobPrimaryPayer,

    /// <summary>Claim where this payer is secondary — requires primary EOB.</summary>
    CobSecondaryPayer,

    /// <summary>Claim where this payer is tertiary — requires primary and secondary EOBs.</summary>
    CobTertiaryPayer,

    /// <summary>COB determined by the birthday rule (dependent child).</summary>
    CobBirthdayRule,

    /// <summary>COB determined by the gender rule (legacy plans).</summary>
    CobGenderRule,

    /// <summary>Medicare as secondary payer (MSP).</summary>
    CobMedicareSecondary,

    // Retro-Eligibility (8,000 claims)

    /// <summary>Member added retroactively — claim should be reprocessed as covered.</summary>
    RetroEligibilityAdd,

    /// <summary>Member terminated retroactively — claim should be recouped.</summary>
    RetroEligibilityTermination,

    /// <summary>Member coverage changed retroactively — benefit plan swap.</summary>
    RetroEligibilityCoverageChange,

    // Newborn (6,000 claims)

    /// <summary>Newborn auto-adjudication under mother's coverage.</summary>
    NewbornAutoAdjudication,

    /// <summary>Newborn claim linked to mother's delivery claim.</summary>
    NewbornMotherClaimLink,

    /// <summary>Newborn services within first 30 days of life.</summary>
    NewbornFirstThirtyDays,

    // Prior Authorization (8,000 claims)

    /// <summary>Prior auth required and authorization is on file.</summary>
    PriorAuthRequired_AuthOnFile,

    /// <summary>Prior auth required but no authorization exists.</summary>
    PriorAuthRequired_NoAuth,

    /// <summary>Prior auth required but authorization has expired.</summary>
    PriorAuthRequired_ExpiredAuth,

    /// <summary>Prior auth on file but for a different provider.</summary>
    PriorAuthRequired_WrongProvider,

    /// <summary>Prior auth on file but for a different procedure.</summary>
    PriorAuthRequired_WrongProcedure,

    // Subrogation (4,000 claims)

    /// <summary>Accident-related claim subject to subrogation.</summary>
    SubrogationAccidentRelated,

    /// <summary>Workers' compensation claim — pend for subrogation review.</summary>
    SubrogationWorkersComp,

    /// <summary>Third-party liability claim — pend for subrogation review.</summary>
    SubrogationThirdPartyLiability,

    // Behavioral Health (6,000 claims)

    /// <summary>Behavioral health benefit carved out to a separate vendor.</summary>
    BehavioralHealthCarveOut,

    /// <summary>Behavioral health benefit carved in to the medical plan.</summary>
    BehavioralHealthCarveIn,

    /// <summary>Mental health parity check — quantitative treatment limit.</summary>
    BehavioralHealthParityCheck,

    // Medicaid Subprogram (6,000 claims)

    /// <summary>Medicaid TANF (Temporary Assistance for Needy Families).</summary>
    MedicaidTANF,

    /// <summary>Medicaid SSI (Supplemental Security Income).</summary>
    MedicaidSSI,

    /// <summary>Children's Health Insurance Program (CHIP).</summary>
    MedicaidCHIP,

    /// <summary>Dual eligible — Medicare primary, Medicaid secondary.</summary>
    MedicaidDualEligible,

    /// <summary>Medicaid spend-down — member must meet liability threshold.</summary>
    MedicaidSpendDown
}
