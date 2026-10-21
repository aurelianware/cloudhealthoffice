namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// X12 Claim Adjustment Reason Codes (CARC) and Remittance Advice Remark
/// Codes (RARC) the adjudication pipeline stamps on a claim it denies or
/// rejects, so every persisted denial carries a code the 835 can report.
///
/// <para>
/// Format follows the rest of the pipeline: the bare code, no group prefix
/// (<c>"16"</c>, not <c>"CO-16"</c> or <c>"CARC_16"</c>) — see
/// <see cref="BenefitCalculationStage.MemberNotEligibleCarc"/> and
/// <see cref="DuplicateClaimStage.DuplicateCarc"/>. payment-service's
/// <c>CarcRarcMappingService</c> reports
/// <see cref="Models.AdjudicationResult.DenialReasonCode"/> in the claim
/// header CAS with group code <c>CO</c> (provider liability), and
/// <see cref="Models.AdjudicationResult.RemarkCodes"/> in MOA/MIA. Every code
/// here is a contractual/payer-initiated denial, so <c>CO</c> is the right
/// group for all of them and no explicit <see cref="Models.ClaimAdjustmentReason"/>
/// entry is needed to change it.
/// </para>
/// </summary>
public static class AdjudicationDenialCodes
{
    // ── CARCs ────────────────────────────────────────────────────────

    /// <summary>CARC 16 — claim/service lacks information or has submission/billing error(s). Structural rejects.</summary>
    public const string ClaimLacksInformationCarc = "16";

    /// <summary>CARC 29 — the time limit for filing has expired. Timely filing denials.</summary>
    public const string TimelyFilingExpiredCarc = "29";

    /// <summary>CARC 22 — this care may be covered by another payer per coordination of benefits.</summary>
    public const string CoveredByAnotherPayerCarc = "22";

    /// <summary>CARC 242 — services not provided by network/primary care providers. Out-of-network denials.</summary>
    public const string NotNetworkProviderCarc = "242";

    /// <summary>
    /// CARC B7 — this provider was not certified/eligible to be paid for this
    /// procedure/service on this date of service. Credentialing denials (also
    /// the ProviderIntegrity exclusion default).
    /// </summary>
    public const string ProviderNotCertifiedCarc = "B7";

    /// <summary>
    /// CARC 236 — procedure/modifier combination not compatible with another
    /// procedure on the same day per NCCI. Claim-level fallback for an NCCI
    /// Deny when the edit carried no suggested CARC (the engine itself
    /// suggests 97 / B20 for PTP edits and 151 for MUE).
    /// </summary>
    public const string NcciIncompatibleCarc = "236";

    /// <summary>CARC 96 — non-covered charge(s). Benefit-side denial fallback; always paired with a RARC.</summary>
    public const string NonCoveredCarc = "96";

    // ── RARCs ────────────────────────────────────────────────────────

    /// <summary>RARC MA130 — claim contains incomplete/invalid information and is unprocessable; submit a new claim.</summary>
    public const string UnprocessableClaimRarc = "MA130";

    /// <summary>RARC MA04 — secondary payment cannot be considered without the primary payer's identity/payment information.</summary>
    public const string MissingPrimaryPayerRarc = "MA04";

    /// <summary>RARC N257 — missing/incomplete/invalid billing provider primary identifier.</summary>
    public const string MissingBillingProviderIdRarc = "N257";

    /// <summary>RARC N382 — missing/incomplete/invalid patient identifier.</summary>
    public const string MissingPatientIdRarc = "N382";

    /// <summary>RARC N130 — consult plan benefit documents/guidelines for restrictions for this service.</summary>
    public const string ConsultPlanBenefitsRarc = "N130";

    // ── PersistenceStage safety-net defaults ─────────────────────────

    /// <summary>
    /// Code the <see cref="PersistenceStage"/> safety net assigns to a Reject
    /// that reached persistence without one. A Reject is by definition a
    /// pre-adjudication "unprocessable" disposition, which X12 reports as
    /// CO-16 + MA130 (provider may correct and resubmit).
    /// </summary>
    public const string DefaultRejectCarc = ClaimLacksInformationCarc;

    /// <inheritdoc cref="DefaultRejectCarc"/>
    public const string DefaultRejectRarc = UnprocessableClaimRarc;

    /// <summary>
    /// Code the <see cref="PersistenceStage"/> safety net assigns to a Deny
    /// that reached persistence without one: CO-96 (non-covered) + N130.
    /// It is the most general adjudicated-denial CARC and makes no claim about
    /// a specific cause (unlike 16, which asserts the submission was
    /// defective). CARC 96 requires a RARC, hence N130.
    /// </summary>
    public const string DefaultDenyCarc = NonCoveredCarc;

    /// <inheritdoc cref="DefaultDenyCarc"/>
    public const string DefaultDenyRarc = ConsultPlanBenefitsRarc;

    private const int MaxDenialReasonLength = 500;

    /// <summary>
    /// Stamps the claim-level denial code (and RARC, when given) on the
    /// in-flight adjudication result. The stage that denies owns the code,
    /// so an existing code is replaced; the reason is set for the examiner
    /// and the EOB.
    /// </summary>
    public static void Apply(ClaimAdjudicationContext context, string carc, string? reason, string? rarc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(carc);

        var result = context.AdjudicationResult;
        result.DenialReasonCode = carc;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            result.DenialReason = reason.Length > MaxDenialReasonLength
                ? reason[..MaxDenialReasonLength]
                : reason;
        }
        AddRemark(context, rarc);
    }

    /// <summary>Appends a RARC to the claim-level remark codes once.</summary>
    public static void AddRemark(ClaimAdjudicationContext context, string? rarc)
    {
        if (string.IsNullOrWhiteSpace(rarc)) return;
        var remarks = context.AdjudicationResult.RemarkCodes;
        if (!remarks.Contains(rarc, StringComparer.Ordinal))
        {
            remarks.Add(rarc);
        }
    }
}
