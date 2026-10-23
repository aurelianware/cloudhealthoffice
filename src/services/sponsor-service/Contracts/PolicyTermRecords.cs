using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SponsorService.Contracts;

/// <summary>
/// One contract year of a policy's terms (docs/architecture/plan-configuration.md
/// §9b): the rates, contribution, eligibility, waiting period, termination and
/// retro rules, life events and documentation requirements as they stand for
/// one contract period.
///
/// <para>
/// This is the record §9b means by "a lasting policy with its terms versioned
/// per contract term". The policy does not change at renewal; a new version of
/// its terms starts on the renewal date. Anything resolved for a date uses the
/// version in force then, which is why the version is keyed on the contract
/// period rather than carrying its own dates: the contract term owns the
/// dates, the status and the signed document, and these terms are what that
/// term covers.
/// </para>
/// </summary>
public sealed class PolicyTerms
{
    /// <summary><c>{policyId}|{periodStart:yyyyMMdd}</c>, the policy id already being tenant-scoped.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("groupNumber")]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>The policy these terms belong to, by its id.</summary>
    [JsonPropertyName("policyId")]
    public string PolicyId { get; set; } = string.Empty;

    /// <summary>
    /// The contract period these terms are for, named by its start date — the
    /// same date that identifies the <see cref="ContractTerm"/>. Fixed: a
    /// different period is a different version.
    /// </summary>
    [JsonPropertyName("periodStart")]
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// What a member pays, by tier. Where a class sets its own rates, that
    /// class's table is used instead; see <see cref="RatesFor"/>.
    /// </summary>
    [JsonPropertyName("rates")]
    public RateTable? Rates { get; set; }

    /// <summary>
    /// Employer and employee shares. Optional in §9b, and needed only where
    /// CHO bills the member directly — COBRA, retirees, direct bill.
    /// </summary>
    [JsonPropertyName("contribution")]
    public ContributionTerms? Contribution { get; set; }

    /// <summary>
    /// Who is eligible, where the policy has no classes or a class says
    /// nothing of its own. Not a fallback for a class that does not exist on
    /// the policy — see <see cref="EligibilityFor"/>.
    /// </summary>
    [JsonPropertyName("eligibility")]
    public EligibilityTerms? Eligibility { get; set; }

    /// <summary>Per-class rates and eligibility, by class code.</summary>
    [JsonPropertyName("classTerms")]
    public List<ClassTerms> ClassTerms { get; set; } = new();

    /// <summary>When coverage starts for a new hire.</summary>
    [JsonPropertyName("enrollment")]
    public EnrollmentTerms? Enrollment { get; set; }

    /// <summary>When coverage ends, and how far back a change may be dated.</summary>
    [JsonPropertyName("termination")]
    public TerminationTerms? Termination { get; set; }

    /// <summary>
    /// The qualifying events this contract allows, with how long a member has
    /// to report each one and when the resulting coverage starts.
    /// </summary>
    [JsonPropertyName("lifeEvents")]
    public List<LifeEventTerms> LifeEvents { get; set; } = new();

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

    public static string IdFor(string policyId, DateTime periodStart) => $"{policyId}|{periodStart:yyyyMMdd}";

    /// <summary>
    /// The rates for a class: the class's own table where it has one, else the
    /// policy's. Null where neither is set, which is a policy whose rates have
    /// not been entered yet rather than a policy that is free.
    /// </summary>
    public RateTable? RatesFor(string? classCode) =>
        ClassTermsFor(classCode)?.Rates ?? Rates;

    /// <summary>
    /// The eligibility rules for a class: the class's own where it has them,
    /// else the policy's.
    /// </summary>
    public EligibilityTerms? EligibilityFor(string? classCode) =>
        ClassTermsFor(classCode)?.Eligibility ?? Eligibility;

    /// <summary>
    /// Whether these terms say anything about <paramref name="classCode"/>.
    /// A caller asking about a class the terms do not carry must not be handed
    /// the policy-wide rules as though they applied: a member placed in a class
    /// nobody wrote terms for would be enrolled and rated under rules that were
    /// written for somebody else. Ask this first, and treat false as "not
    /// stated" rather than resolving it.
    /// </summary>
    public bool Covers(string classCode) =>
        !string.IsNullOrWhiteSpace(classCode) && ClassTermsFor(classCode) is not null;

    private ClassTerms? ClassTermsFor(string? classCode) =>
        string.IsNullOrWhiteSpace(classCode)
            ? null
            : ClassTerms.FirstOrDefault(c => string.Equals(c.ClassCode, classCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What one class is rated and enrolled on, where it differs from the policy.</summary>
public sealed class ClassTerms
{
    /// <summary>The class's code on the policy (<see cref="SponsorClass.Code"/>).</summary>
    [JsonPropertyName("classCode")]
    public string ClassCode { get; set; } = string.Empty;

    /// <summary>This class's own rates, where they differ from the policy's.</summary>
    [JsonPropertyName("rates")]
    public RateTable? Rates { get; set; }

    /// <summary>This class's own eligibility rules — the usual reason a class exists.</summary>
    [JsonPropertyName("eligibility")]
    public EligibilityTerms? Eligibility { get; set; }
}

/// <summary>
/// How a contract divides a household for rating (§9b). The structure decides
/// which tiers a rate table must carry: a table missing one of them has a
/// household shape nobody can bill.
/// </summary>
public enum TierStructure
{
    /// <summary>Employee, family.</summary>
    TwoTier,
    /// <summary>Employee, employee plus one, family.</summary>
    ThreeTier,
    /// <summary>Employee, employee plus spouse, employee plus children, family.</summary>
    FourTier,
}

/// <summary>A household shape a rate is quoted for.</summary>
public enum RateTier
{
    Employee,
    EmployeePlusOne,
    EmployeePlusSpouse,
    EmployeePlusChildren,
    Family,
}

/// <summary>The rate for each tier of a structure, per billing period.</summary>
public sealed class RateTable
{
    [JsonPropertyName("structure")]
    public TierStructure Structure { get; set; }

    /// <summary>The rate for each tier the structure calls for.</summary>
    [JsonPropertyName("tiers")]
    public List<TierRate> Tiers { get; set; } = new();

    /// <summary>
    /// The rate for a household shape, or null where the table does not quote
    /// that tier — which, for a complete table, means the shape belongs to a
    /// different structure.
    /// </summary>
    public decimal? For(RateTier tier) =>
        Tiers.FirstOrDefault(t => t.Tier == tier)?.Amount;

    /// <summary>The tiers a structure must quote, and exactly those.</summary>
    public static IReadOnlyList<RateTier> TiersOf(TierStructure structure) => structure switch
    {
        TierStructure.TwoTier => [RateTier.Employee, RateTier.Family],
        TierStructure.ThreeTier => [RateTier.Employee, RateTier.EmployeePlusOne, RateTier.Family],
        TierStructure.FourTier =>
            [RateTier.Employee, RateTier.EmployeePlusSpouse, RateTier.EmployeePlusChildren, RateTier.Family],
        _ => [],
    };
}

public sealed class TierRate
{
    [JsonPropertyName("tier")]
    public RateTier Tier { get; set; }

    /// <summary>The rate per billing period, in the plan's currency.</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
}

/// <summary>
/// Who pays what (§9b). Needed where CHO bills the member directly; on a list
/// bill the employer remits the whole premium and the split is their own
/// business.
/// </summary>
public sealed class ContributionTerms
{
    /// <summary>The employer's share of the employee-only rate, as a percentage.</summary>
    [JsonPropertyName("employerSharePercentEmployee")]
    public decimal? EmployerSharePercentEmployee { get; set; }

    /// <summary>The employer's share of the cost above the employee-only rate.</summary>
    [JsonPropertyName("employerSharePercentDependents")]
    public decimal? EmployerSharePercentDependents { get; set; }
}

/// <summary>Who may enrol (§9b: hours, employee types, dependent rules).</summary>
public sealed class EligibilityTerms
{
    /// <summary>The hours a week an employee must work to be eligible.</summary>
    [JsonPropertyName("minimumHoursPerWeek")]
    public decimal? MinimumHoursPerWeek { get; set; }

    /// <summary>
    /// The employee types this covers, as the contract names them: full time,
    /// part time, seasonal, union. Empty means the contract does not restrict
    /// by type.
    /// </summary>
    [JsonPropertyName("employeeTypes")]
    public List<string> EmployeeTypes { get; set; } = new();

    [JsonPropertyName("dependents")]
    public DependentTerms? Dependents { get; set; }
}

/// <summary>Which dependents a contract covers (§9b).</summary>
public sealed class DependentTerms
{
    /// <summary>
    /// The age a child ages off at. The ACA floor is 26 and a contract may be
    /// more generous, so a lower limit is refused rather than clamped.
    /// </summary>
    [JsonPropertyName("childAgeLimit")]
    public int? ChildAgeLimit { get; set; }

    /// <summary>Whether a disabled child stays covered past the age limit.</summary>
    [JsonPropertyName("coverDisabledBeyondAgeLimit")]
    public bool CoverDisabledBeyondAgeLimit { get; set; }

    [JsonPropertyName("coverDomesticPartners")]
    public bool CoverDomesticPartners { get; set; }

    /// <summary>
    /// Whether a child over the age limit stays covered while a student. Rare
    /// since the ACA raised the floor to 26, and still written into some
    /// contracts.
    /// </summary>
    [JsonPropertyName("coverStudentsBeyondAgeLimit")]
    public bool CoverStudentsBeyondAgeLimit { get; set; }
}

/// <summary>When a new hire's coverage starts (§9b's waiting period and effective-date rule).</summary>
public sealed class EnrollmentTerms
{
    /// <summary>
    /// Days between hire and the end of the waiting period. The ACA caps a
    /// group waiting period at 90 days, so a longer one is refused.
    /// </summary>
    [JsonPropertyName("waitingPeriodDays")]
    public int? WaitingPeriodDays { get; set; }

    [JsonPropertyName("effectiveDateRule")]
    public EffectiveDateRule EffectiveDateRule { get; set; } = EffectiveDateRule.DayAfterWaitingPeriod;

    /// <summary>
    /// How far back a late enrolment may be dated. Separate from the
    /// retroactive <i>termination</i> window, because a plan that will accept a
    /// member two months late will rarely drop one two months back.
    /// </summary>
    [JsonPropertyName("retroEnrollmentDays")]
    public int? RetroEnrollmentDays { get; set; }
}

/// <summary>How the waiting period turns into an effective date (§9b).</summary>
public enum EffectiveDateRule
{
    /// <summary>The day after the waiting period ends.</summary>
    DayAfterWaitingPeriod,
    /// <summary>The first of the month after the waiting period ends — the common one.</summary>
    FirstOfMonthAfterWaitingPeriod,
    /// <summary>The first of the month the waiting period ends in, or the next, whichever it falls on.</summary>
    FirstOfMonthCoincidingOrFollowing,
    /// <summary>The date of hire, with no waiting period.</summary>
    DateOfHire,
}

/// <summary>When coverage ends, and how far back (§9b).</summary>
public sealed class TerminationTerms
{
    [JsonPropertyName("rule")]
    public TerminationRule Rule { get; set; } = TerminationRule.EndOfMonth;

    /// <summary>
    /// How far back a termination may be dated. A plan that allows this is
    /// accepting that claims already paid in the window may have to be
    /// recovered.
    /// </summary>
    [JsonPropertyName("retroTerminationDays")]
    public int? RetroTerminationDays { get; set; }
}

public enum TerminationRule
{
    /// <summary>The last day of the month the event falls in.</summary>
    EndOfMonth,
    /// <summary>The day of the event itself.</summary>
    DateOfEvent,
}

/// <summary>
/// A qualifying event the contract allows, with the window a member has to
/// report it and when the coverage it opens starts (§9b).
/// </summary>
public sealed class LifeEventTerms
{
    /// <summary>The event, upper case: <c>BIRTH</c>, <c>MARRIAGE</c>, <c>LOSS_OF_COVERAGE</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Days the member has to report it. Federal special-enrolment windows are
    /// floors: at least 30 days for a group plan, and at least 60 after losing
    /// Medicaid or CHIP, so a shorter window for those events is refused.
    /// </summary>
    [JsonPropertyName("reportingWindowDays")]
    public int? ReportingWindowDays { get; set; }

    [JsonPropertyName("coverageStartRule")]
    public LifeEventCoverageStart CoverageStartRule { get; set; } = LifeEventCoverageStart.DateOfEvent;

    /// <summary>
    /// The proof the contract requires for this event, as document-type codes
    /// (§9b's documentation requirements). Enforced at enrollment, not here.
    /// </summary>
    [JsonPropertyName("documentationRequired")]
    public List<string> DocumentationRequired { get; set; } = new();
}

public enum LifeEventCoverageStart
{
    /// <summary>The day of the event — the rule for a birth or an adoption.</summary>
    DateOfEvent,
    /// <summary>The first of the month after the event.</summary>
    FirstOfMonthAfterEvent,
    /// <summary>The day the lost coverage ended, so there is no gap.</summary>
    DayAfterPriorCoverageEnds,
}

/// <summary>
/// The federal floors §9b names, as the values this record refuses to go below.
/// Stated here rather than inline so that the number and the reason sit
/// together and a reviewer can check both.
///
/// <para>
/// These are not yet <c>SettingFloor</c> entries in the configuration catalog
/// with citations to the regulation text — that is a decision still open with
/// Mark (step 3 proposal, question 4). What is safe to do meanwhile is refuse
/// the writes that would be unlawful either way, with the reason named, since a
/// refusal cannot create a non-compliant contract and a silent clamp would hide
/// one.
/// </para>
/// </summary>
public static class ContractFloors
{
    /// <summary>A group health plan's waiting period, in days (ACA).</summary>
    public const int MaxWaitingPeriodDays = 90;

    /// <summary>The age a child may stay on a parent's plan to (ACA).</summary>
    public const int MinChildAgeLimit = 26;

    /// <summary>A special-enrolment window for a group plan, in days.</summary>
    public const int MinSpecialEnrollmentDays = 30;

    /// <summary>A special-enrolment window after losing Medicaid or CHIP, in days.</summary>
    public const int MinMedicaidLossEnrollmentDays = 60;

    /// <summary>
    /// The life events whose window is the 60-day Medicaid and CHIP one rather
    /// than the general 30 days.
    /// </summary>
    public static readonly IReadOnlySet<string> MedicaidLossEvents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LOSS_OF_MEDICAID",
            "LOSS_OF_CHIP",
            "CHIP_ELIGIBILITY",
            "MEDICAID_ELIGIBILITY",
        };

    /// <summary>
    /// The shortest window this event may have. Events that are not special
    /// enrolment under federal law — a plan's own rule for a transfer, say —
    /// are not held to either floor, so only the events named are checked.
    /// </summary>
    public static int? MinWindowFor(string code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : MedicaidLossEvents.Contains(code.Trim()) ? MinMedicaidLossEnrollmentDays
        : SpecialEnrollmentEvents.Contains(code.Trim()) ? MinSpecialEnrollmentDays
        : null;

    /// <summary>
    /// The events federal law gives a special-enrolment window to. A contract
    /// may name others of its own, which these floors say nothing about.
    /// </summary>
    public static readonly IReadOnlySet<string> SpecialEnrollmentEvents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MARRIAGE",
            "BIRTH",
            "ADOPTION",
            "PLACEMENT_FOR_ADOPTION",
            "LOSS_OF_COVERAGE",
            "DIVORCE",
            "DEATH",
        };
}
