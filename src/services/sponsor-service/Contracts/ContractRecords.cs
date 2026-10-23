using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SponsorService.Contracts;

/// <summary>
/// A benefit plan the sponsor offers, under its own policy (sub-group) number
/// (docs/architecture/plan-configuration.md §9b).
///
/// <para>
/// A policy is a <b>lasting</b> record: it continues across renewals, and the
/// terms that change each year are versions hung off it rather than a new
/// policy. That is the whole point of the record. Ending and recreating
/// policies every renewal would term and re-add every member, risk a coverage
/// break, pend claims around the boundary, reissue ID cards and split the
/// history — so the fields that identify the policy are fixed once it exists,
/// and a change to any of them means a new policy with the old one ended
/// (§9b, "when a policy continues and when it is replaced").
/// </para>
/// </summary>
public sealed class SponsorPolicy
{
    /// <summary><c>{tenantId}|{groupNumber}|{policyNumber}</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The sponsor's group number, which identifies the account and does not
    /// change at renewal (§9b). It stays on the sponsor record; this is the
    /// link back to it.
    /// </summary>
    [JsonPropertyName("groupNumber")]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// The policy (sub-group) number under the group, e.g. <c>123456-0001</c>
    /// for the PPO and <c>123456-0002</c> for the HMO. This is the number
    /// printed on ID cards and sent as the group number on the 834
    /// (<c>REF*1L</c>) and the 837 (<c>SBR03</c>), so it is fixed once the
    /// policy exists.
    /// </summary>
    [JsonPropertyName("policyNumber")]
    public string PolicyNumber { get; set; } = string.Empty;

    /// <summary>What staff call it: "PPO 500", "HMO Base". Changeable.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The benefit plan sold under this policy, by its business plan id. Fixed:
    /// a different plan is a different policy. Plan-<i>year</i> benefit changes
    /// (deductibles, copays) are versions of the benefit plan itself, where
    /// accumulators reset; this points at the plan, and the version in force on
    /// a date is the plan's own business (§9b).
    /// </summary>
    [JsonPropertyName("benefitPlanId")]
    public string BenefitPlanId { get; set; } = string.Empty;

    /// <summary>
    /// The program the plan is sold under (tenant-service, §3), where the
    /// sponsor's plans are not all from one program. Fixed, and nullable: a
    /// policy written before programs existed, or one whose program nobody has
    /// stated, carries none, and a reader must treat null as "not stated"
    /// rather than reading it off the sponsor.
    /// </summary>
    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }

    /// <summary>
    /// The eligibility classes under this policy: hourly and salaried, a
    /// management class, a union class. Their rules arrive with the terms;
    /// here they are the nodes that a class-level setting hangs off.
    /// </summary>
    [JsonPropertyName("classes")]
    public List<SponsorClass> Classes { get; set; } = new();

    /// <summary>First day the policy is offered.</summary>
    [JsonPropertyName("effectiveFrom")]
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>
    /// Last day it is offered: set to the day before a renewal when the policy
    /// is being replaced rather than continued.
    /// </summary>
    [JsonPropertyName("effectiveTo")]
    public DateTime? EffectiveTo { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonIgnore]
    public long Revision { get; set; }

    public static string IdFor(string tenantId, string groupNumber, string policyNumber) =>
        $"{tenantId}|{groupNumber}|{policyNumber}";

    /// <summary>Whether the policy is offered on <paramref name="on"/>.</summary>
    public bool IsOffered(DateTime on) =>
        Active
        && (EffectiveFrom is null || EffectiveFrom.Value.Date <= on.Date)
        && (EffectiveTo is null || EffectiveTo.Value.Date >= on.Date);
}

/// <summary>
/// An eligibility class under a policy (§9b): the group of employees a set of
/// eligibility rules and rates applies to.
/// </summary>
public sealed class SponsorClass
{
    /// <summary>The code within the policy, upper case: <c>HOURLY</c>, <c>SALARIED</c>, <c>MGMT</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    /// <summary>
    /// How plan configuration names this class as a node
    /// (<c>SettingLevel.Class</c>): the policy's id and the class code. One
    /// spelling, computed here, so that a setting written at class level and a
    /// class read from this record cannot drift apart.
    /// </summary>
    public static string ScopeIdFor(string policyId, string classCode) => $"{policyId}|{classCode}";
}

/// <summary>
/// Where a contract term stands (§9b). The statuses record what people did;
/// the dates say what the term covers.
/// </summary>
public enum ContractTermStatus
{
    /// <summary>Being entered: plans, tiers, rates, eligibility, settings.</summary>
    Draft,
    /// <summary>Something is out of range and underwriting is deciding.</summary>
    InUnderwriting,
    /// <summary>Signed, with the signed document attached.</summary>
    Signed,
    /// <summary>In force: reached on the term's own start date, never before it.</summary>
    Active,
    /// <summary>Over, or never taken up.</summary>
    Ended,
}

/// <summary>
/// The group agreement for one contract period, usually a plan year (§9b): the
/// dates, where it stands, and the signed document. The terms it covers — the
/// rates, eligibility and settings — are versions on each policy, so that a
/// renewal does not move a member's policy.
/// </summary>
public sealed class ContractTerm
{
    /// <summary><c>{tenantId}|{groupNumber}|{periodStart:yyyyMMdd}</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("groupNumber")]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>First day of the contract period. Fixed: it is part of the term's identity.</summary>
    [JsonPropertyName("periodStart")]
    public DateTime PeriodStart { get; set; }

    /// <summary>Last day of the contract period.</summary>
    [JsonPropertyName("periodEnd")]
    public DateTime PeriodEnd { get; set; }

    /// <summary>
    /// The day the next term starts, where it is not simply the day after
    /// <see cref="PeriodEnd"/> — an off-cycle renewal, say.
    /// </summary>
    [JsonPropertyName("renewalDate")]
    public DateTime? RenewalDate { get; set; }

    [JsonPropertyName("status")]
    public ContractTermStatus Status { get; set; } = ContractTermStatus.Draft;

    /// <summary>
    /// The signed agreement in the document store, by its document id. Required
    /// to sign: a term marked signed with nothing attached is a claim nobody
    /// can check.
    /// </summary>
    [JsonPropertyName("signedDocumentId")]
    public string? SignedDocumentId { get; set; }

    [JsonPropertyName("signedOn")]
    public DateTime? SignedOn { get; set; }

    /// <summary>Why the term last moved, as the caller gave it.</summary>
    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; set; }

    /// <summary>
    /// How the group is billed for this contract year (§9b). Replaces the flat
    /// <c>Sponsor.BillingInfo</c>, which carried one set of terms for the
    /// account however many years it ran.
    /// </summary>
    [JsonPropertyName("billing")]
    public BillingTerms? Billing { get; set; }

    /// <summary>
    /// What the quote was written against (§9b): minimum participation and
    /// minimum employer contribution. The thresholds are data here; who routes
    /// a term that misses them and who decides is the approval workflow, which
    /// §9b leaves to enrollment step 4.
    /// </summary>
    [JsonPropertyName("underwriting")]
    public UnderwritingConditions? Underwriting { get; set; }

    /// <summary>Every status change, oldest first.</summary>
    [JsonPropertyName("statusHistory")]
    public List<ContractTermStatusChange> StatusHistory { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonIgnore]
    public long Revision { get; set; }

    public static string IdFor(string tenantId, string groupNumber, DateTime periodStart) =>
        $"{tenantId}|{groupNumber}|{periodStart:yyyyMMdd}";

    /// <summary>Whether <paramref name="on"/> falls in this term's period.</summary>
    public bool Covers(DateTime on) => PeriodStart.Date <= on.Date && on.Date <= PeriodEnd.Date;

    /// <summary>
    /// Whether this is the term to resolve a date against: signed or active,
    /// and covering the date. A draft is not something to bill or enrol from,
    /// and an ended one is over.
    /// </summary>
    public bool IsInForce(DateTime on) =>
        Status is ContractTermStatus.Signed or ContractTermStatus.Active && Covers(on);
}

/// <summary>Who sends the bill and who it is sent to (§9b).</summary>
public enum BillingMethod
{
    /// <summary>
    /// CHO bills the employer for the whole group, listing each member. The
    /// usual arrangement, and the one the employer reconciles against.
    /// </summary>
    ListBill,

    /// <summary>
    /// The employer calculates and remits its own premium, and CHO reconciles
    /// what arrives against what it expected.
    /// </summary>
    SelfBill,
}

/// <summary>
/// How the group is billed for one contract year (§9b): the method, how often,
/// when it falls due and how long it may run late.
/// </summary>
public sealed class BillingTerms
{
    [JsonPropertyName("method")]
    public BillingMethod Method { get; set; } = BillingMethod.ListBill;

    [JsonPropertyName("frequency")]
    public Models.BillingFrequency Frequency { get; set; } = Models.BillingFrequency.Monthly;

    /// <summary>
    /// The day of the month the premium falls due. A day past the 28th is
    /// kept as written and read through <see cref="DueDateIn"/>, which lands
    /// it on the last day of a month that is shorter.
    /// </summary>
    [JsonPropertyName("dueDay")]
    public int DueDay { get; set; } = 1;

    /// <summary>
    /// Days the premium may run late before the group is suspended. This is
    /// the field §9b moves off the sponsor record.
    /// </summary>
    [JsonPropertyName("gracePeriodDays")]
    public int GracePeriodDays { get; set; } = 30;

    /// <summary>Where the remittance is applied. An operational detail, not an agreed term.</summary>
    [JsonPropertyName("billingAccountNumber")]
    public string? BillingAccountNumber { get; set; }

    /// <summary>ACH, wire, check, card. An operational detail, not an agreed term.</summary>
    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// The date the premium falls due in a given month. A due day of 31 means
    /// the end of the month rather than a date that does not exist, so it is
    /// clamped to the month's length — the alternative is every consumer
    /// getting February wrong in its own way.
    /// </summary>
    public DateTime DueDateIn(int year, int month) =>
        new(year, month, Math.Min(DueDay, DateTime.DaysInMonth(year, month)));
}

/// <summary>
/// What the quote assumed (§9b). A renewal review checks the group against
/// these; the deciding is the workflow §9b leaves to enrollment step 4.
/// </summary>
public sealed class UnderwritingConditions
{
    /// <summary>The share of eligible employees who must enrol, as a percentage.</summary>
    [JsonPropertyName("minimumParticipationPercent")]
    public decimal? MinimumParticipationPercent { get; set; }

    /// <summary>The share of the employee-only premium the employer must pay, as a percentage.</summary>
    [JsonPropertyName("minimumEmployerContributionPercent")]
    public decimal? MinimumEmployerContributionPercent { get; set; }

    /// <summary>The fewest enrolled employees the quote holds for.</summary>
    [JsonPropertyName("minimumEnrolledEmployees")]
    public int? MinimumEnrolledEmployees { get; set; }

    /// <summary>Why the term was sent to underwriting, or what was agreed there.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class ContractTermStatusChange
{
    [JsonPropertyName("from")]
    public ContractTermStatus From { get; set; }

    [JsonPropertyName("to")]
    public ContractTermStatus To { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("changedBy")]
    public string ChangedBy { get; set; } = string.Empty;

    [JsonPropertyName("changedAt")]
    public DateTime ChangedAt { get; set; }
}

/// <summary>
/// The moves a contract term may make (§9b's flow: a new group or renewal
/// opens a draft → the account manager enters the terms → anything out of
/// range goes to underwriting → on signature the document is attached and the
/// term activates on its start date).
///
/// <para>
/// Underwriting can send a term back to draft, because a condition it refuses
/// is something the account manager has to change. A draft or a term in
/// underwriting can end, because a group that does not take up a quote should
/// not leave a draft open for ever. Nothing comes back from Ended: a term that
/// was over and is wanted again is a new term, with its own dates.
/// </para>
/// </summary>
public static class ContractTermTransitions
{
    private static readonly IReadOnlyDictionary<ContractTermStatus, ContractTermStatus[]> Allowed =
        new Dictionary<ContractTermStatus, ContractTermStatus[]>
        {
            [ContractTermStatus.Draft] = [ContractTermStatus.InUnderwriting, ContractTermStatus.Signed, ContractTermStatus.Ended],
            [ContractTermStatus.InUnderwriting] = [ContractTermStatus.Draft, ContractTermStatus.Signed, ContractTermStatus.Ended],
            [ContractTermStatus.Signed] = [ContractTermStatus.Active, ContractTermStatus.Ended],
            [ContractTermStatus.Active] = [ContractTermStatus.Ended],
            [ContractTermStatus.Ended] = [],
        };

    public static IReadOnlyList<ContractTermStatus> AllowedFrom(ContractTermStatus from) =>
        Allowed.TryGetValue(from, out var to) ? to : [];

    public static bool IsAllowed(ContractTermStatus from, ContractTermStatus to) => AllowedFrom(from).Contains(to);
}
