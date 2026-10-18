using AppealsService.Models;

namespace AppealsService.Services.RulePacks;

/// <summary>
/// One State's appeals, grievance, provider-dispute and recoupment rules for
/// one line of business, as versioned, effective-dated data
/// (docs/architecture/appeals-case-model.md §4). Every clock names its unit
/// and cites the rule it comes from. Packs ship as embedded JSON under
/// <c>RulePacks/</c> and are validated against the federal floor at startup
/// (<see cref="RulePackValidator"/>): a pack can tighten a federal clock,
/// never loosen it.
/// </summary>
public sealed class RulePack
{
    /// <summary>Stable id, e.g. <c>tx-medicaid</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Two-letter State code, upper case.</summary>
    public string State { get; init; } = string.Empty;

    public LineOfBusiness LineOfBusiness { get; init; }

    /// <summary>The program the pack describes, e.g. "STAR and CHIP managed care".</summary>
    public string Program { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public DateOnly EffectiveFrom { get; init; }

    /// <summary>Last day the pack applies; <c>null</c> while current.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>When the values were last checked against their sources.</summary>
    public DateOnly VerifiedOn { get; init; }

    /// <summary>Source id → citation. Every <see cref="Clock.Source"/> must resolve here.</summary>
    public Dictionary<string, RuleSource> Sources { get; init; } = new();

    public MemberAppealRules MemberAppeal { get; init; } = new();

    public MemberGrievanceRules MemberGrievance { get; init; } = new();

    public ProviderDisputeRules ProviderDispute { get; init; } = new();

    /// <summary>Effective-dated; the latest entry in effect on a date applies.</summary>
    public List<RecoupmentRule> Recoupment { get; init; } = new();

    /// <summary>Where a rule and a contract (or member materials) disagree: the value enforced and the other.</summary>
    public List<RuleConflict> Conflicts { get; init; } = new();

    /// <summary>Values not yet verified against primary text.</summary>
    public List<string> ToConfirm { get; init; } = new();

    public bool AppliesOn(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo);
}

public sealed class RuleSource
{
    public string Citation { get; init; } = string.Empty;
    public string? Url { get; init; }
}

/// <summary>A deadline: an amount of a unit, measured from an event, citing its rule.</summary>
public sealed class Clock
{
    public int Amount { get; init; }
    public ClockUnit Unit { get; init; }

    /// <summary>The event the clock runs from. Most run from receipt.</summary>
    public ClockStart From { get; init; } = ClockStart.Receipt;

    /// <summary>Key into <see cref="RulePack.Sources"/>.</summary>
    public string Source { get; init; } = string.Empty;

    public string? Note { get; init; }

    public override string ToString() => $"{Amount} {Unit} from {From}";
}

public enum ClockUnit
{
    Hours,
    CalendarDays,

    /// <summary>Monday–Friday, skipping the tenant's holiday calendar.</summary>
    BusinessDays,

    /// <summary>
    /// Monday–Friday, skipping the tenant's holiday calendar. Kept distinct
    /// from <see cref="BusinessDays"/> so a pack records the term its rule uses.
    /// </summary>
    WorkingDays,

    /// <summary>Calendar months. Filing windows and lookbacks only.</summary>
    Months
}

public enum ClockStart
{
    Receipt,

    /// <summary>When the information needed to decide arrived (e.g. Illinois expedited appeals).</summary>
    InformationReceived,

    /// <summary>The plan's decision or resolution notice.</summary>
    Decision,

    /// <summary>The adverse benefit determination notice.</summary>
    Notice
}

public sealed class MemberAppealRules
{
    public Clock FilingWindow { get; init; } = new();
    public Clock? Acknowledgment { get; init; }
    public Clock? AcknowledgmentExpedited { get; init; }
    public Clock Resolution { get; init; } = new();
    public Clock ResolutionExpedited { get; init; } = new();
    public ExtensionRule Extension { get; init; } = new();
    public MissedDeadlineOutcome OnMissed { get; init; } = MissedDeadlineOutcome.DeemedExhaustion;

    /// <summary>Time the member has to request a State fair hearing, from the plan's decision.</summary>
    public Clock FairHearingRequestWindow { get; init; } = new();

    /// <summary>Time to ask for benefits to continue, from the notice (federal: later of 10 days or the effective date).</summary>
    public Clock? ContinuationRequestWindow { get; init; }

    /// <summary>The level chain after the plan's own appeal.</summary>
    public List<LevelStep> Chain { get; init; } = new();

    public List<string> Notes { get; init; } = new();
}

public sealed class ExtensionRule
{
    /// <summary>Calendar days the plan may add; 0 when no extension is allowed.</summary>
    public int MaxDays { get; init; }

    /// <summary>True when the State must approve the extension before the deadline (Ohio).</summary>
    public bool RequiresStateApproval { get; init; }

    /// <summary>Written notice to the member when the plan extends.</summary>
    public Clock? WrittenNotice { get; init; }

    public string Source { get; init; } = string.Empty;
}

public enum MissedDeadlineOutcome
{
    /// <summary>The member may go straight to the next level (42 CFR 438.408(c)(3)).</summary>
    DeemedExhaustion,

    /// <summary>The adverse determination is reversed (New York utilization review appeals, PHL §4904).</summary>
    Reversal
}

public sealed class MemberGrievanceRules
{
    public Clock? Acknowledgment { get; init; }
    public Clock Resolution { get; init; } = new();
    public Clock? ResolutionExpedited { get; init; }

    /// <summary>Clocks for particular kinds of grievance (e.g. Ohio access grievances, 2 business days).</summary>
    public List<NamedClock> Variants { get; init; } = new();

    public ExtensionRule? Extension { get; init; }
    public List<string> Notes { get; init; } = new();
}

public sealed class NamedClock
{
    public string Name { get; init; } = string.Empty;
    public Clock Clock { get; init; } = new();
}

public sealed class ProviderDisputeRules
{
    /// <summary>True when the State sets no process and each plan's contract governs.</summary>
    public bool PlanDefined { get; init; }

    public List<LevelStep> Chain { get; init; } = new();
    public List<NamedClock> Clocks { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

public sealed class LevelStep
{
    public string Name { get; init; } = string.Empty;
    public LevelOwner Owner { get; init; }
    public Clock? RequestWindow { get; init; }
    public Clock? Decision { get; init; }
    public bool Binding { get; init; }
    public string? Source { get; init; }
    public string? Note { get; init; }
}

public enum LevelOwner
{
    Plan,
    State,
    IndependentReviewer,
    Regulator,
    Arbitrator,
    Court
}

public sealed class RecoupmentRule
{
    public RecoupmentHold Hold { get; init; }

    /// <summary>First day this rule applies; <c>null</c> means from the pack's start.</summary>
    public DateOnly? EffectiveFrom { get; init; }

    public string Summary { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

public enum RecoupmentHold
{
    /// <summary>Collection of a disputed overpayment must wait for the dispute.</summary>
    Required,

    /// <summary>Required for some recoveries (e.g. fraud findings), not all.</summary>
    Partial,

    /// <summary>The researched rules state no hold.</summary>
    NotStated,

    /// <summary>Not determined from primary sources; treat as to confirm.</summary>
    Unknown
}

public sealed class RuleConflict
{
    public string Item { get; init; } = string.Empty;
    public string Enforced { get; init; } = string.Empty;
    public string Alternative { get; init; } = string.Empty;
    public List<string> Sources { get; init; } = new();
}
