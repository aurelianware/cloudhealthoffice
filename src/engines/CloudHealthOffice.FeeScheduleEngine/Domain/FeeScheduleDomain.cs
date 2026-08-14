namespace CloudHealthOffice.FeeScheduleEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// FEE SCHEDULE DOMAIN TYPES
//
// These model the concepts that govern how providers are paid:
//   - Fee schedule: the lookup table of procedure → rate
//   - Provider contract: which schedule applies to a given provider/plan
//   - Modifier rules: how procedure modifiers adjust the base rate
//
// QNXT equivalents:
//   FeeSchedule       → FS_FEE_SCHEDULE + FS_FEE_SCHEDULE_LINE
//   ProviderContract  → CONTRACT + CONTRACT_LINE + PROV_PLAN
//   NetworkStatus     → PROV_PLAN.IN_NETWORK_IND
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// The type of fee schedule — drives how base rates are calculated.
/// </summary>
public enum FeeScheduleType
{
    /// <summary>
    /// Medicare Physician Fee Schedule.
    /// Rate = (WorkRVU × WorkGPCI + PeRVU × PeGPCI + MpRVU × MpGPCI) × ConversionFactor
    /// </summary>
    MedicareMpfs,

    /// <summary>
    /// Medicare Outpatient Prospective Payment System (APC-based).
    /// Hospitals use APC codes; CHO stores pre-calculated APC rates.
    /// </summary>
    MedicareOpps,

    /// <summary>
    /// Medicaid — often expressed as a percentage of Medicare MPFS.
    /// Rate = MedicareMpfsRate × PercentOfMedicare
    /// </summary>
    Medicaid,

    /// <summary>
    /// Commercial contracted rates — flat rates negotiated per provider/group.
    /// Rate = FeeScheduleLine.Rate (flat dollar amount per procedure).
    /// </summary>
    Commercial,

    /// <summary>
    /// Custom/payer-defined schedule. Rate is the flat Line.Rate value.
    /// </summary>
    Custom,

    /// <summary>
    /// Usual, Customary, and Reasonable — fallback when no contracted rate exists.
    /// For out-of-network: typically billed charges or a UCR database amount.
    /// CHO uses billed charges when no other source is available.
    /// </summary>
    Ucr,

    /// <summary>
    /// Per diem — inpatient daily rate, not per-procedure.
    /// AllowedAmount = PerDiemRate × LengthOfStay
    /// </summary>
    PerDiem,

    /// <summary>
    /// DRG case rate — fixed payment per inpatient stay regardless of services.
    /// </summary>
    Drg,

    /// <summary>
    /// Capitation — provider receives a fixed PMPM; claims are tracking-only.
    /// AllowedAmount = 0 (no fee-for-service payment).
    /// </summary>
    Capitation
}

/// <summary>
/// How a fee schedule line's Rate field is interpreted.
/// </summary>
public enum FeeScheduleRateType
{
    /// <summary>Dollar amount per unit of service.</summary>
    FlatRate,

    /// <summary>
    /// Medicare RVU-based. The engine computes:
    /// (WorkRVU × WorkGPCI + PeRVU × PeGPCI + MpRVU × MpGPCI) × ConversionFactor
    /// </summary>
    Rvu,

    /// <summary>Percentage of billed charges (e.g., 0.80 = 80% of billed).</summary>
    PercentOfBilled,

    /// <summary>Percentage of Medicare MPFS rate (e.g., 1.10 = 110% of Medicare).</summary>
    PercentOfMedicare
}

/// <summary>Business origin of a fee schedule, independent of its calculation type.</summary>
public enum FeeScheduleSourceType
{
    PracticeCharge,
    PayerContract,
    PublicGovernment,
    Reference,
    DevelopmentFixture
}

/// <summary>
/// Provider's network status for a given plan on a given date.
/// </summary>
public enum NetworkStatus
{
    /// <summary>Contracted in-network provider.</summary>
    InNetwork,

    /// <summary>Non-contracted out-of-network provider.</summary>
    OutOfNetwork,

    /// <summary>Participating provider (Medicare PAR).</summary>
    Participating,

    /// <summary>Non-participating (Medicare non-PAR) — may balance-bill.</summary>
    NonParticipating,

    /// <summary>Provider not found in any contract — treat as out-of-network.</summary>
    Unknown
}

/// <summary>
/// The source used to determine a claim line's allowed amount.
/// Stored in PricingResult for audit and reporting.
/// </summary>
public enum RateSource
{
    /// <summary>Matched a procedure-specific contracted rate line.</summary>
    ContractedRate,

    /// <summary>Medicare MPFS RVU-based calculation.</summary>
    MedicareMpfs,

    /// <summary>Medicare OPPS APC rate.</summary>
    MedicareOpps,

    /// <summary>Medicaid fee schedule (percent of Medicare).</summary>
    Medicaid,

    /// <summary>Plan-default fee schedule (no provider-specific contract).</summary>
    PlanDefault,

    /// <summary>Billed charges (UCR fallback — no fee schedule matched).</summary>
    BilledCharges,

    /// <summary>Per diem rate × length of stay.</summary>
    PerDiem,

    /// <summary>DRG case rate.</summary>
    Drg,

    /// <summary>Capitation — no fee-for-service payment.</summary>
    Capitation
}

/// <summary>
/// Standard CMS modifier codes that affect payment rates.
/// Not all modifiers affect payment; only these are actioned by the rate engine.
/// </summary>
public static class PaymentModifiers
{
    /// <summary>Professional component only.</summary>
    public const string ProfessionalComponent = "26";

    /// <summary>Technical component only.</summary>
    public const string TechnicalComponent = "TC";

    /// <summary>Bilateral procedure — 150% of unilateral rate.</summary>
    public const string Bilateral = "50";

    /// <summary>
    /// Multiple procedures — secondary/subsequent procedures reduced to 50%.
    /// The engine applies this automatically based on procedure rank.
    /// </summary>
    public const string MultipleProcedures = "51";

    /// <summary>Reduced services — typically 50% of base rate.</summary>
    public const string ReducedServices = "52";

    /// <summary>Discontinued procedure — typically 50% of base rate.</summary>
    public const string DiscontinuedProcedure = "53";

    /// <summary>Co-surgery — each surgeon receives 62.5% of single-surgeon rate.</summary>
    public const string CoSurgery = "62";

    /// <summary>Assistant surgeon — receives 16% of primary surgeon's rate.</summary>
    public const string AssistantSurgeon = "80";

    /// <summary>
    /// Assistant-at-surgery (PA, NP, CRNA) — 85% of assistant surgeon rate.
    /// </summary>
    public const string AssistantAtSurgery = "AS";

    /// <summary>Increased procedural services — 125% of base rate.</summary>
    public const string IncreasedComplexity = "22";
}
