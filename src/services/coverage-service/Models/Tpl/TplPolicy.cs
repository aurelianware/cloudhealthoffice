using MongoDB.Bson.Serialization.Attributes;

namespace CoverageService.Models.Tpl;

/// <summary>
/// One third-party liability (TPL) resource the state knows a Medicaid member
/// has: a commercial policy, Medicare, TRICARE, a dental-only plan, a casualty
/// claim. Medicaid is the payer of last resort (42 CFR 433.139), so claims for
/// services this resource covers are cost-avoided or paid and chased.
///
/// <para>
/// Loaded from the state's TPL file (Texas HHSC third-party resource file,
/// Medi-Cal OHC data from the eligibility record) or recorded by the plan when
/// it discovers coverage. Kept apart from <see cref="Coverage.OtherInsurance"/>:
/// a member can hold several TPL resources at once, each with its own scope and
/// dates, and the state's file is the system of record for them.
/// </para>
///
/// <para>
/// Rows are never deleted. When a full state file no longer carries a policy,
/// or the state reports it removed, the row is ended (<see cref="EndDate"/>
/// and <see cref="Status"/>) so claims adjudicated against it can still be
/// explained.
/// </para>
/// </summary>
public sealed class TplPolicy
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The member as CHO knows them: the 834 subscriber id
    /// (<see cref="Coverage.MemberId"/>), which for Medicaid is the state
    /// Medicaid id (Texas Medicaid number, Medi-Cal CIN).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Two-letter state whose Medicaid program reported the resource.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Where the row came from: a layout id such as <c>tx-tpr</c>
    /// or <c>ca-ohc</c>, or <c>plan-discovered</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The state's own carrier code, when the file has one (Medi-Cal
    /// carrier code, Texas carrier id).</summary>
    public string? CarrierCode { get; set; }

    public string CarrierName { get; set; } = string.Empty;

    public string? PolicyNumber { get; set; }

    public string? GroupNumber { get; set; }

    public string? PolicyholderName { get; set; }

    /// <summary>The coverage code exactly as the state sent it (a Medi-Cal
    /// OHC code such as <c>K</c>, a Texas coverage type), kept for audit and
    /// for rules that key on the state's own code.</summary>
    public string? StateCoverageCode { get; set; }

    /// <summary>The services this resource covers, mapped from
    /// <see cref="StateCoverageCode"/> by the layout's code table.</summary>
    public TplScope Scope { get; set; }

    /// <summary>True when the state says Medicaid pays first and recovers
    /// later for every service, whatever the scope (Medi-Cal OHC code
    /// <c>A</c>). Cost avoidance never applies to such a resource.</summary>
    public bool PayAndChaseOnly { get; set; }

    public DateTime EffectiveDate { get; set; }

    public DateTime? EndDate { get; set; }

    public TplPolicyStatus Status { get; set; } = TplPolicyStatus.Active;

    /// <summary>Why the row was ended, when it was.</summary>
    public string? EndReason { get; set; }

    /// <summary>The file that last confirmed the row.</summary>
    public string? SourceFileName { get; set; }

    public DateTime FirstLoadedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    public string? LastUpdatedBy { get; set; }

    /// <summary>When the plan learned of the coverage (plan-discovered rows).</summary>
    public DateTime? DiscoveredAt { get; set; }

    /// <summary>What the plan still owes the state for this row: report a
    /// newly found policy (<c>Add</c>) or ask for a wrong state record to be
    /// removed (<c>Remove</c>). Null once reported, or for rows the state sent.</summary>
    public TplStateAction? PendingStateAction { get; set; }

    /// <summary>Why the plan asked the state to remove the record.</summary>
    public string? PendingStateActionReason { get; set; }

    /// <summary>When the report to the state is due (California: 10 calendar
    /// days after discovery, APL 21-002). Null where the state sets no clock.</summary>
    public DateTime? StateReportDueBy { get; set; }

    public DateTime? ReportedToStateAt { get; set; }

    /// <summary>The state's reference for the report (batch or form id).</summary>
    public string? StateReportReference { get; set; }

    /// <summary>When a state file first carried a row the plan had found itself.</summary>
    public DateTime? StateConfirmedAt { get; set; }

    /// <summary>Natural key used to match a file row to a stored row:
    /// member, state, carrier, policy and start date. Two rows from the same
    /// state for the same policy that start on different dates are separate
    /// spans.</summary>
    public string NaturalKey { get; set; } = string.Empty;

    public static string BuildNaturalKey(
        string memberId, string state, string? carrierCode, string carrierName,
        string? policyNumber, DateTime effectiveDate) =>
        string.Join('|',
            memberId.Trim().ToUpperInvariant(),
            state.Trim().ToUpperInvariant(),
            (string.IsNullOrWhiteSpace(carrierCode) ? carrierName : carrierCode).Trim().ToUpperInvariant(),
            (policyNumber ?? string.Empty).Trim().ToUpperInvariant(),
            effectiveDate.ToString("yyyy-MM-dd"));

    /// <summary>True when the resource covers <paramref name="date"/>.</summary>
    public bool IsActiveOn(DateTime date) =>
        Status != TplPolicyStatus.Removed
        && EffectiveDate.Date <= date.Date
        && (EndDate is null || EndDate.Value.Date >= date.Date);
}

public enum TplStateAction
{
    Add,
    Remove,
}

public enum TplPolicyStatus
{
    Active,
    /// <summary>The span ended on <see cref="TplPolicy.EndDate"/> (the file
    /// said so, or a full file stopped carrying it).</summary>
    Ended,
    /// <summary>The state removed the record as never valid; claims should
    /// not be cost-avoided against it on any date.</summary>
    Removed,
}

/// <summary>
/// What a TPL resource pays for. A claim line is cost-avoided only when the
/// resource's scope covers the line's service category.
/// </summary>
[Flags]
public enum TplScope
{
    None = 0,
    /// <summary>Inpatient and outpatient facility services.</summary>
    Hospital = 1 << 0,
    /// <summary>Physician and other professional services.</summary>
    Professional = 1 << 1,
    Pharmacy = 1 << 2,
    Dental = 1 << 3,
    Vision = 1 << 4,
    BehavioralHealth = 1 << 5,
    LongTermCare = 1 << 6,
    /// <summary>Durable medical equipment and supplies.</summary>
    Dme = 1 << 7,
    /// <summary>Accident, auto, workers' compensation or other casualty
    /// coverage; only liable for injury-related services.</summary>
    Casualty = 1 << 8,
    MedicarePartA = 1 << 9,
    MedicarePartB = 1 << 10,
    /// <summary>Medicare Advantage.</summary>
    MedicarePartC = 1 << 11,
    MedicarePartD = 1 << 12,

    /// <summary>Hospital, professional, behavioral health and DME: what a
    /// comprehensive major-medical policy pays for.</summary>
    Medical = Hospital | Professional | BehavioralHealth | Dme,
    /// <summary>A comprehensive policy with drug coverage.</summary>
    Comprehensive = Medical | Pharmacy,
}
