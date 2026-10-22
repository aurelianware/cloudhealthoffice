using System.Text.Json.Serialization;
using CloudHealthOffice.ReferenceData.Domain;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.FeeScheduleEngine.Models;

// ═══════════════════════════════════════════════════════════════════
// REIMBURSEMENT TERMS — the canonical contract model (ADR 016 §2)
// ═══════════════════════════════════════════════════════════════════
//
// One contract document carries an ordered list of reimbursement terms.
// For a claim line the engine takes the first term whose scope matches and
// whose effective dates cover the service date, and prices the line by that
// term's method. A contract with no terms prices exactly as before, from
// ProviderContract.FeeScheduleId and ContractLines (ADR 016 phase 2: the
// canonical schema is additive and changes no behaviour until terms are
// written).
//
// Why a discriminator and not a class hierarchy: the method-specific fields
// live on ReimbursementTerm beside a Method enum rather than in polymorphic
// subdocuments. A term is persisted and read back by three stores and two
// services; a flat document needs no BSON discriminator registration and no
// System.Text.Json polymorphism attributes, and a field a method does not use
// is simply null. Term.Validate() is the rule that only the fields belonging
// to Method may be set.

/// <summary>
/// How a reimbursement term pays a claim line. Exactly one method per term
/// (ADR 016 §2).
/// </summary>
public enum ReimbursementMethod
{
    /// <summary>
    /// Pay from <see cref="ReimbursementTerm.FeeScheduleId"/>, the rate
    /// multiplied by <see cref="ReimbursementTerm.Multiplier"/> (1.0 = the
    /// schedule's own rate). The <c>FfsRateConfig</c> shape:
    /// multiplier = <c>FeeSchedulePercentage</c>.
    /// </summary>
    FeeScheduleRef = 0,

    /// <summary>
    /// Pay a percentage of the Medicare allowed amount: price the line from
    /// <see cref="ReimbursementTerm.BaseScheduleId"/> (a Medicare MPFS or OPPS
    /// schedule) and multiply by <see cref="ReimbursementTerm.Percent"/>.
    /// </summary>
    PercentOfMedicare = 1,

    /// <summary>
    /// Pay a percentage of the provider's billed charge for the line. No fee
    /// schedule is read, so the line is never unpriceable for want of a rate.
    /// </summary>
    PercentOfBilled = 2,

    /// <summary>
    /// Inpatient case rate: price from a <see cref="Domain.FeeScheduleType.Drg"/>
    /// schedule, which pays the case rate (or base rate × DRG weight) once per
    /// stay, allocated across the claim's lines.
    /// </summary>
    CaseRate = 3,

    /// <summary>
    /// Per diem: price from a <see cref="Domain.FeeScheduleType.PerDiem"/>
    /// schedule. An all-inclusive schedule rate × length of stay is paid once
    /// per stay; a line-level daily rate prices each accommodation line by its
    /// units.
    /// </summary>
    PerDiem = 4,

    /// <summary>
    /// Capitated: the service is covered by the provider's PMPM payment, so the
    /// claim line is priced at $0 with <see cref="Domain.RateSource.Capitation"/>.
    /// </summary>
    Capitation = 5,
}

/// <summary>
/// The kind of claim a term applies to. An institutional line is one the
/// caller flagged <see cref="PricingRequest.IsInstitutional"/> or that carries a
/// valid <see cref="PricingRequest.BillType"/>; inpatient and outpatient are
/// read from the type of bill's classification digit
/// (<see cref="InstitutionalClaimKind"/>).
/// </summary>
public enum ContractClaimType
{
    /// <summary>Any claim — the default, and the only value that needs no claim context.</summary>
    Any = 0,

    /// <summary>Professional (837P / CMS-1500) lines only.</summary>
    Professional = 1,

    /// <summary>Any institutional (837I / UB-04) line, inpatient or outpatient.</summary>
    Institutional = 2,

    /// <summary>
    /// Institutional inpatient only (type of bill classification 1 or 2, e.g.
    /// 11x hospital inpatient Part A, 12x inpatient Part B). A line with no
    /// readable type of bill does not match.
    /// </summary>
    InstitutionalInpatient = 3,

    /// <summary>
    /// Institutional outpatient only (type of bill classification 3 or 4, e.g.
    /// 13x hospital outpatient). A line with no readable type of bill does not
    /// match.
    /// </summary>
    InstitutionalOutpatient = 4,
}

/// <summary>
/// Which claim lines a <see cref="ReimbursementTerm"/> covers. Every condition
/// set must hold (AND); an unset condition matches everything. An empty scope
/// matches every line, which is the shape of a contract's last-resort term.
/// </summary>
[BsonIgnoreExtraElements]
public class ReimbursementTermScope
{
    /// <summary>CPT/HCPCS range start, inclusive. Null = no procedure-code condition.</summary>
    public string? ProcedureCodeFrom { get; set; }

    /// <summary>
    /// CPT/HCPCS range end, inclusive. Null with a non-null
    /// <see cref="ProcedureCodeFrom"/> = open-ended, as
    /// <see cref="ProviderContractLine"/> reads it.
    /// </summary>
    public string? ProcedureCodeTo { get; set; }

    /// <summary>
    /// UB-04 revenue codes this term covers. Empty = no revenue-code condition.
    /// Compared on the four-digit form, so "120" and "0120" are one code.
    /// </summary>
    public List<string> RevenueCodes { get; set; } = new();

    /// <summary>
    /// CMS place-of-service codes this term covers. Empty = no place-of-service
    /// condition. Not read for an institutional line, whose
    /// <see cref="PricingRequest.PlaceOfServiceCode"/> holds the 837I CLM05-1
    /// facility type code rather than a place of service; such a line matches a
    /// term that names places of service only when it names none.
    /// </summary>
    public List<string> PlacesOfService { get; set; } = new();

    /// <summary>The kind of claim this term covers. Default <see cref="ContractClaimType.Any"/>.</summary>
    public ContractClaimType ClaimType { get; set; } = ContractClaimType.Any;

    /// <summary>
    /// True when no condition is set, so the term covers every line. Not persisted.
    /// </summary>
    [JsonIgnore]
    [BsonIgnore]
    public bool IsUnconditional
        => ProcedureCodeFrom is null
           && RevenueCodes.Count == 0
           && PlacesOfService.Count == 0
           && ClaimType == ContractClaimType.Any;
}

/// <summary>
/// Whether a term's method takes part in the modifier adjustments the engine
/// applies. These are the two <c>FfsRateConfig</c> toggles
/// (<c>MultipleProcedureReduction</c>, <c>BilateralAdjustment</c>); both
/// default to true, which is the engine's behaviour without a term.
/// </summary>
[BsonIgnoreExtraElements]
public class TermModifierRules
{
    /// <summary>
    /// False suppresses the CMS multiple procedure reduction for lines priced
    /// by this term: the line is left out of the 100/50/50 ranking and modifier
    /// 51 does not reduce it.
    /// </summary>
    public bool MultipleProcedureReduction { get; set; } = true;

    /// <summary>
    /// False suppresses the bilateral (modifier 50) 150% adjustment for lines
    /// priced by this term, whatever the rate line's
    /// <see cref="FeeScheduleLine.BilateralAdjustmentApplies"/> says.
    /// </summary>
    public bool BilateralAdjustment { get; set; } = true;
}

/// <summary>
/// Contracted stop-loss provision: above a billed-charge threshold for the
/// stay, the payer pays a percentage of the charges above it. <b>Stored but not
/// evaluated</b> (ADR 016 §2). A term carrying it pends the claim rather than
/// paying as if the provision did not exist.
/// </summary>
[BsonIgnoreExtraElements]
public class StopLossTerm
{
    /// <summary>Billed charges for the stay above which stop-loss applies.</summary>
    public decimal ThresholdBilled { get; set; }

    /// <summary>Share of the charges above the threshold the payer pays (0.80 = 80%).</summary>
    public decimal PercentAboveThreshold { get; set; }
}

/// <summary>
/// Contracted cost outlier provision, in the shape of the Medicare IPPS outlier
/// payment. <b>Stored but not evaluated</b> (ADR 016 §2): the engine has no
/// cost-to-charge ratio and no grouper. A term carrying it pends the claim.
/// </summary>
[BsonIgnoreExtraElements]
public class OutlierTerm
{
    /// <summary>Fixed loss threshold added to the case rate before an outlier is paid.</summary>
    public decimal FixedLossThreshold { get; set; }

    /// <summary>Share of the cost above the threshold the payer pays (0.80 = 80%).</summary>
    public decimal MarginalCostFactor { get; set; }
}

/// <summary>
/// One reimbursement term of a contract: which lines it covers, when it is in
/// force, and how those lines are paid (ADR 016 §2).
///
/// <para>
/// The terms of a contract are ordered and the first match wins, so a contract
/// reads as its own rate sheet: the carve-outs first, the default last. A term
/// whose scope is empty covers every line, which is how a contract's
/// last-resort schedule is expressed — the shape the legacy
/// <see cref="ProviderContract.FeeScheduleId"/> and
/// <see cref="ProviderContract.ContractLines"/> map to.
/// </para>
///
/// <para>
/// Extra elements are ignored, so a document written by a later version (a
/// method or condition this build does not know) still deserializes. Such a
/// document can price differently from the version that wrote it, which is why
/// <see cref="Validate"/> runs at the write boundary rather than here.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class ReimbursementTerm
{
    /// <summary>
    /// Stable id within the contract, for audit and for an amendment to name
    /// the term it changed. Not a lookup key: the engine selects by order.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>What this term is, for the portal and the pricing audit trail.</summary>
    public string? Description { get; set; }

    /// <summary>Which lines this term covers. Empty scope = every line.</summary>
    public ReimbursementTermScope Scope { get; set; } = new();

    /// <summary>
    /// First service date this term prices. Null = from the contract's own
    /// effective date (the contract is already date-filtered by the repository).
    /// </summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>Last service date this term prices, inclusive. Null = open-ended.</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>How lines matching this term are paid. Exactly one method.</summary>
    public ReimbursementMethod Method { get; set; } = ReimbursementMethod.FeeScheduleRef;

    // ── Method fields ─────────────────────────────────────────────

    /// <summary>
    /// The fee schedule this term prices from. Required for
    /// <see cref="ReimbursementMethod.FeeScheduleRef"/>,
    /// <see cref="ReimbursementMethod.CaseRate"/> and
    /// <see cref="ReimbursementMethod.PerDiem"/>.
    /// </summary>
    public string? FeeScheduleId { get; set; }

    /// <summary>
    /// Multiplier on the rate the schedule produced, for the schedule-based
    /// methods (<see cref="ReimbursementMethod.FeeScheduleRef"/>,
    /// <see cref="ReimbursementMethod.CaseRate"/>,
    /// <see cref="ReimbursementMethod.PerDiem"/>). 1.0 = the schedule's own rate.
    /// Applied to the base amount, before modifier adjustments, units, the
    /// multiple procedure reduction and lesser-of.
    /// </summary>
    public decimal Multiplier { get; set; } = 1.0m;

    /// <summary>
    /// The Medicare schedule to price from, for
    /// <see cref="ReimbursementMethod.PercentOfMedicare"/>.
    /// </summary>
    public string? BaseScheduleId { get; set; }

    /// <summary>
    /// The percentage, for <see cref="ReimbursementMethod.PercentOfMedicare"/>
    /// (of the Medicare allowed amount) and
    /// <see cref="ReimbursementMethod.PercentOfBilled"/> (of the line's billed
    /// charge). 1.10 = 110%.
    /// </summary>
    public decimal? Percent { get; set; }

    /// <summary>
    /// The capitation rate configuration this term is covered by, for
    /// <see cref="ReimbursementMethod.Capitation"/>. Carried for audit; the
    /// engine does not read it, because a capitated line is priced at $0
    /// whatever the PMPM is.
    /// </summary>
    public string? CapitationRateConfigId { get; set; }

    // ── Term options ──────────────────────────────────────────────

    /// <summary>
    /// True when this term's lines are paid in addition to a per-stay rate on
    /// the same claim, rather than being covered by it — an implant or a drug
    /// carved out of a DRG case rate. Carried for audit and for the per-stay
    /// allocation to tell a carve-out line from a stay line; a carve-out term
    /// is priced like any other term.
    /// </summary>
    public bool CarveOut { get; set; }

    /// <summary>
    /// Which modifier adjustments apply to this term's lines. Null = both apply,
    /// the engine's behaviour without a term.
    /// </summary>
    public TermModifierRules? ModifierRules { get; set; }

    /// <summary>
    /// Overrides <see cref="ProviderContract.LesserOfBilledCharges"/> for this
    /// term's lines. Null = the contract's setting applies.
    /// </summary>
    public bool? LesserOfBilled { get; set; }

    /// <summary>Stop-loss provision. Stored, not evaluated: pends the claim.</summary>
    public StopLossTerm? StopLoss { get; set; }

    /// <summary>Cost outlier provision. Stored, not evaluated: pends the claim.</summary>
    public OutlierTerm? Outlier { get; set; }

    // ── Derived ───────────────────────────────────────────────────

    /// <summary>
    /// Why a line priced by this term cannot be paid yet, or null when it can.
    /// A provision the engine stores but does not evaluate (stop-loss, cost
    /// outlier) would change the allowed amount, so the line is pended rather
    /// than paid as though the provision were not in the contract. Not persisted.
    /// </summary>
    [JsonIgnore]
    [BsonIgnore]
    public string? UnevaluatedProvisionReason
        => (StopLoss, Outlier) switch
        {
            (not null, not null) =>
                "contract term carries stop-loss and cost outlier provisions, which the pricing engine " +
                "stores but does not yet evaluate; the line is pended rather than paid without them",
            (not null, null) =>
                "contract term carries a stop-loss provision, which the pricing engine stores but does " +
                "not yet evaluate; the line is pended rather than paid without it",
            (null, not null) =>
                "contract term carries a cost outlier provision, which the pricing engine stores but does " +
                "not yet evaluate; the line is pended rather than paid without it",
            _ => null,
        };

    /// <summary>
    /// True when <paramref name="serviceDate"/> falls within this term's own
    /// effective dates. The contract's dates are applied by the repository; a
    /// term's dates narrow the window further (a mid-term rate amendment).
    /// </summary>
    public bool IsEffectiveOn(DateTime serviceDate)
        => (EffectiveDate is null || serviceDate.Date >= EffectiveDate.Value.Date)
           && (TermDate is null || serviceDate.Date <= TermDate.Value.Date);

    /// <summary>
    /// The reasons this term is not a valid document, empty when it is. Checked
    /// where a term is written (the contract admin API), not where it is read:
    /// the engine must stay able to read a document it did not write, and pends
    /// a line it cannot price rather than rejecting the stored contract.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TermDate is not null && EffectiveDate is not null && TermDate.Value.Date < EffectiveDate.Value.Date)
            errors.Add("termDate is before effectiveDate");

        if (Scope.ProcedureCodeFrom is null && Scope.ProcedureCodeTo is not null)
            errors.Add("scope.procedureCodeTo is set without scope.procedureCodeFrom");

        if (Scope.ProcedureCodeFrom is not null && Scope.ProcedureCodeTo is not null
            && StringComparer.OrdinalIgnoreCase.Compare(Scope.ProcedureCodeTo, Scope.ProcedureCodeFrom) < 0)
            errors.Add("scope.procedureCodeTo is before scope.procedureCodeFrom");

        if (Scope.PlacesOfService.Count > 0
            && Scope.ClaimType is ContractClaimType.Institutional
                or ContractClaimType.InstitutionalInpatient
                or ContractClaimType.InstitutionalOutpatient)
        {
            errors.Add(
                "scope names places of service and an institutional claim type; an institutional line " +
                "carries a facility type code rather than a place of service, so the term would never match");
        }

        switch (Method)
        {
            case ReimbursementMethod.FeeScheduleRef:
                if (string.IsNullOrWhiteSpace(FeeScheduleId))
                    errors.Add("feeScheduleId is required for a feeScheduleRef term");
                if (Multiplier <= 0m)
                    errors.Add("multiplier must be greater than zero");
                break;

            case ReimbursementMethod.PercentOfMedicare:
                if (string.IsNullOrWhiteSpace(BaseScheduleId))
                    errors.Add("baseScheduleId is required for a percentOfMedicare term");
                if (Percent is null or <= 0m)
                    errors.Add("percent must be greater than zero for a percentOfMedicare term");
                break;

            case ReimbursementMethod.PercentOfBilled:
                if (Percent is null or <= 0m)
                    errors.Add("percent must be greater than zero for a percentOfBilled term");
                break;

            case ReimbursementMethod.CaseRate:
            case ReimbursementMethod.PerDiem:
                if (string.IsNullOrWhiteSpace(FeeScheduleId))
                    errors.Add($"feeScheduleId is required for a {Method} term");
                if (Multiplier <= 0m)
                    errors.Add("multiplier must be greater than zero");
                break;

            case ReimbursementMethod.Capitation:
                break;

            default:
                errors.Add($"method {(int)Method} is not a reimbursement method this build knows");
                break;
        }

        foreach (var field in UnusedFieldsForMethod())
            errors.Add($"{field} is set but is not a field of a {Method} term");

        return errors;
    }

    private IEnumerable<string> UnusedFieldsForMethod()
    {
        var usesSchedule = Method is ReimbursementMethod.FeeScheduleRef
            or ReimbursementMethod.CaseRate or ReimbursementMethod.PerDiem;

        if (FeeScheduleId is not null && !usesSchedule)
            yield return "feeScheduleId";
        if (BaseScheduleId is not null && Method != ReimbursementMethod.PercentOfMedicare)
            yield return "baseScheduleId";
        if (Percent is not null
            && Method is not (ReimbursementMethod.PercentOfMedicare or ReimbursementMethod.PercentOfBilled))
            yield return "percent";
        if (CapitationRateConfigId is not null && Method != ReimbursementMethod.Capitation)
            yield return "capitationRateConfigId";
    }
}

/// <summary>
/// Reads the institutional claim kind a <see cref="PricingRequest"/> carries,
/// for <see cref="ContractClaimType"/> term scoping.
/// </summary>
public static class InstitutionalClaimKind
{
    /// <summary>
    /// True when the line is institutional: the caller flagged it, or it carries
    /// a well-formed type of bill. The same rule the facility-rate decision uses
    /// (<c>RateResolutionService.IsFacilitySetting</c>).
    /// </summary>
    public static bool IsInstitutional(PricingRequest request)
        => request.IsInstitutional || NubcTypeOfBill.IsValid(request.BillType);

    /// <summary>
    /// True for an inpatient line, false for an outpatient one, null when the
    /// line carries no readable type of bill to tell them apart (a caller that
    /// set <see cref="PricingRequest.IsInstitutional"/> and sent no bill type).
    ///
    /// <para>
    /// The second digit of a type of bill is the bill classification. For the
    /// facility types that have inpatient and outpatient forms — hospital (1),
    /// skilled nursing (2) and, for Medicare, religious non-medical health care
    /// institutions (4) — 1 and 2 are inpatient (Part A and Part B) and 3 and 4
    /// are outpatient. Other classifications (swing beds, intermediate care
    /// levels) and other facility types (home health 3, clinic 7, special
    /// facility 8) have no inpatient / outpatient reading here and come back
    /// null, so a term scoped to inpatient or outpatient does not match them.
    /// </para>
    /// </summary>
    public static bool? IsInpatient(PricingRequest request)
    {
        var tob = NubcTypeOfBill.Normalize(request.BillType);
        if (tob is null)
            return null;

        if (tob[0] is not ('1' or '2' or '4'))
            return null;

        return tob[1] switch
        {
            '1' or '2' => true,
            '3' or '4' => false,
            _ => null,
        };
    }
}
