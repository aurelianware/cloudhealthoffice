using ClaimsService.Models;

namespace ClaimsService.EDI.Florida;

/// <summary>
/// FMMIS (Florida Medicaid Management Information System) Companion Guide
/// constants, enums, and compliance validation for X12 005010 837P/837I
/// encounter submissions to the Florida Agency for Health Care Administration (AHCA).
///
/// <para><b>Key deviations from standard X12 837:</b></para>
/// <list type="bullet">
///   <item>
///     <term>2000B Subscriber Loop</term>
///     <description>
///       All FL Medicaid enrollees are treated as the primary subscriber.
///       NM109 = Medicaid ID. The 2000C dependent loop is never generated,
///       even when the member is a dependent on another policy.
///     </description>
///   </item>
///   <item>
///     <term>2010AA Billing Provider REF*1D</term>
///     <description>
///       An additional REF segment with REF01 = '1D' must be present in the
///       2010AA Billing Provider loop. REF02 carries the provider's Florida
///       Medicaid Provider Number (distinct from NPI).
///     </description>
///   </item>
///   <item>
///     <term>ISA08 Receiver ID</term>
///     <description>
///       ISA08 must be the literal value 'FMMIS' (padded to 15 characters)
///       rather than a payer NPI or clearinghouse ID.
///     </description>
///   </item>
///   <item>
///     <term>File Naming</term>
///     <description>
///       Submission files follow the pattern FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat.
///     </description>
///   </item>
///   <item>
///     <term>Encounter Submission Window</term>
///     <description>
///       Encounters must be submitted within 60 calendar days of adjudication
///       (AHCA MCO contract requirement).
///     </description>
///   </item>
/// </list>
/// </summary>
public static class FmmisCompanionGuide
{
    // ── ISA Header Constants ─────────────────────────────────────────

    /// <summary>
    /// ISA08 Receiver Interchange ID for all FMMIS transmissions.
    /// Must be padded to 15 characters in the ISA segment.
    /// </summary>
    public const string FmmisReceiverId = "FMMIS";

    /// <summary>
    /// ISA05 / ISA07 qualifier for FMMIS transmissions.
    /// ZZ = Mutually Defined (standard for Medicaid agency submissions).
    /// </summary>
    public const string IsaQualifier = "ZZ";

    /// <summary>
    /// ISA11 repetition separator (^ for 5010).
    /// </summary>
    public const char RepetitionSeparator = '^';

    /// <summary>
    /// ISA12 version code for 5010 transactions.
    /// </summary>
    public const string InterchangeVersion = "00501";

    /// <summary>
    /// ISA15 usage indicator. P = Production, T = Test.
    /// </summary>
    public const string ProductionIndicator = "P";
    public const string TestIndicator = "T";

    // ── GS Header Constants ──────────────────────────────────────────

    /// <summary>
    /// GS01 Functional Identifier Code for 837 Health Care Claim.
    /// </summary>
    public const string FunctionalIdCode837 = "HC";

    /// <summary>
    /// GS08 Version / Release / Industry Identifier Code.
    /// 837P = 005010X222A1, 837I = 005010X223A3.
    /// </summary>
    public const string VersionCode837P = "005010X222A1";
    public const string VersionCode837I = "005010X223A3";

    // ── BHT Constants ────────────────────────────────────────────────

    /// <summary>
    /// BHT01 Hierarchical Structure Code for 837.
    /// 0019 = Information Source, Subscriber, Dependent hierarchy.
    /// </summary>
    public const string HierarchicalStructureCode = "0019";

    /// <summary>
    /// BHT02 Transaction Set Purpose Code for encounter submissions.
    /// 18 = Reissue — used by FL FMMIS to distinguish encounter data
    /// from original claim submissions.
    /// </summary>
    public const string EncounterPurposeCode = "18";

    /// <summary>
    /// BHT06 Transaction Type Code. CH = Chargeable.
    /// </summary>
    public const string TransactionTypeChargeable = "CH";

    // ── REF Segment Constants ────────────────────────────────────────

    /// <summary>
    /// REF01 qualifier for Florida Medicaid Provider Number.
    /// Required in the 2010AA Billing Provider loop for all FMMIS submissions.
    /// </summary>
    public const string FlMedicaidProviderRefQualifier = "1D";

    // ── SBR Constants ────────────────────────────────────────────────

    /// <summary>
    /// SBR01 Payer Responsibility Sequence Number Code.
    /// P = Primary. In FMMIS encounters the member is always the primary subscriber.
    /// </summary>
    public const string PayerResponsibilityPrimary = "P";

    /// <summary>
    /// SBR09 Claim Filing Indicator Code for Florida Medicaid.
    /// MC = Medicaid.
    /// </summary>
    public const string ClaimFilingIndicatorMedicaid = "MC";

    // ── Submission Window ────────────────────────────────────────────

    /// <summary>
    /// Maximum number of calendar days after adjudication within which
    /// encounters must be submitted to FMMIS (AHCA MCO contract).
    /// </summary>
    public const int EncounterSubmissionWindowDays = 60;

    // ── NM1 Entity Identifier Codes ──────────────────────────────────

    /// <summary>NM1 entity: 41 = Submitter.</summary>
    public const string EntitySubmitter = "41";
    /// <summary>NM1 entity: 40 = Receiver.</summary>
    public const string EntityReceiver = "40";
    /// <summary>NM1 entity: 85 = Billing Provider.</summary>
    public const string EntityBillingProvider = "85";
    /// <summary>NM1 entity: IL = Insured or Subscriber.</summary>
    public const string EntitySubscriber = "IL";
    /// <summary>NM1 entity: PR = Payer.</summary>
    public const string EntityPayer = "PR";

    // ── Acknowledgment Response Types ────────────────────────────────

    /// <summary>
    /// FMMIS acknowledgment / response transaction types returned after
    /// an 837 encounter submission.
    /// </summary>
    public enum FmmisAcknowledgmentType
    {
        /// <summary>
        /// TA1 — Interchange Acknowledgment. Confirms ISA/IEA envelope integrity.
        /// Returned within minutes of file receipt.
        /// </summary>
        TA1,

        /// <summary>
        /// 997 — Functional Acknowledgment. Confirms GS/GE functional group
        /// structure and segment/element syntax. Returned within 24 hours.
        /// </summary>
        FA997,

        /// <summary>
        /// 999 — Implementation Acknowledgment. Validates transaction set
        /// content against the 005010 implementation guide. Returned within
        /// 24 hours; replaces 997 in 5010.
        /// </summary>
        IA999
    }

    /// <summary>
    /// TA1 Acknowledgment status codes returned by FMMIS.
    /// </summary>
    public enum Ta1StatusCode
    {
        /// <summary>A — Accepted.</summary>
        Accepted,
        /// <summary>E — Accepted But Errors Were Noted.</summary>
        AcceptedWithErrors,
        /// <summary>R — Rejected.</summary>
        Rejected
    }

    /// <summary>
    /// 999 Implementation Acknowledgment status codes.
    /// </summary>
    public enum Ia999StatusCode
    {
        /// <summary>A — Accepted.</summary>
        Accepted,
        /// <summary>E — Accepted But Errors Were Noted.</summary>
        AcceptedWithErrors,
        /// <summary>R — Rejected.</summary>
        Rejected
    }

    // ── Compliance Validation ────────────────────────────────────────

    /// <summary>
    /// Validates that a <see cref="Claim"/> meets all FMMIS Companion Guide
    /// requirements before transformation. Returns an empty list if valid.
    /// </summary>
    /// <param name="claim">The adjudicated claim to validate.</param>
    /// <returns>List of human-readable validation error messages.</returns>
    public static List<string> ValidateFmmisCompliance(Claim claim)
    {
        var errors = new List<string>();

        // ── Line of Business must be Medicaid ──────────────────────
        if (claim.LineOfBusiness != LineOfBusiness.Medicaid)
        {
            errors.Add($"FMMIS submissions require Medicaid line of business; claim has '{claim.LineOfBusiness}'.");
        }

        // ── Claim must be adjudicated ──────────────────────────────
        if (claim.AdjudicatedDate is null)
        {
            errors.Add("Claim has no adjudication date; only adjudicated claims can be submitted as encounters.");
        }

        // ── 60-day encounter submission window ─────────────────────
        if (claim.AdjudicatedDate is not null)
        {
            var daysSinceAdjudication = (DateTime.UtcNow - claim.AdjudicatedDate.Value).TotalDays;
            if (daysSinceAdjudication > EncounterSubmissionWindowDays)
            {
                errors.Add(
                    $"Claim adjudicated {daysSinceAdjudication:F0} days ago; " +
                    $"exceeds FMMIS {EncounterSubmissionWindowDays}-day submission window.");
            }
        }

        // ── Claim must be in a terminal status ─────────────────────
        if (claim.Status is not (ClaimStatus.Paid or ClaimStatus.Approved
            or ClaimStatus.Denied or ClaimStatus.PartiallyPaid))
        {
            errors.Add($"Claim status '{claim.Status}' is not a terminal adjudication status.");
        }

        // ── Member ID required (becomes NM109 Medicaid ID) ─────────
        if (string.IsNullOrWhiteSpace(claim.MemberId))
        {
            errors.Add("MemberId is required (used as Medicaid ID in 2000B subscriber NM109).");
        }

        // ── Billing provider NPI required ──────────────────────────
        if (string.IsNullOrWhiteSpace(claim.BillingProviderNPI))
        {
            errors.Add("BillingProviderNPI is required for the 2010AA loop.");
        }

        // ── At least one claim line ────────────────────────────────
        if (claim.ClaimLines.Count == 0)
        {
            errors.Add("Claim must have at least one service line for encounter submission.");
        }

        // ── At least one diagnosis code ────────────────────────────
        if (claim.DiagnosisCodes.Count == 0)
        {
            errors.Add("Claim must have at least one diagnosis code (HI segment).");
        }

        // ── Claim type must be Professional or Institutional ───────
        if (claim.ClaimType is not (ClaimType.Professional or ClaimType.Institutional))
        {
            errors.Add($"FMMIS encounter submission supports 837P/837I only; claim type is '{claim.ClaimType}'.");
        }

        return errors;
    }
}
