using AppealsService.Models;

namespace AppealsService.Services.RulePacks;

/// <summary>
/// The federal floor one line of business is measured against: the ceilings
/// a pack may tighten but never loosen, the windows federal law fixes, and
/// the citation to quote when a clock is clamped.
///
/// <para>Every ceiling here comes from
/// <see cref="AppealResponseDeadlinePolicy"/> rather than being restated, so
/// the policy that appeals-service already enforces on real appeals, and the
/// floor that rule packs are validated against, cannot drift apart.</para>
///
/// <para>Packs were Medicaid-only until 2026-10-23: the validator refused
/// any other line of business, because the floor was written as Medicaid
/// constants. Medicare Advantage is the second, and the shape is now general
/// enough that Part D or Marketplace is a row here rather than a rewrite.</para>
/// </summary>
public sealed record RulePackFloor
{
    public required LineOfBusiness LineOfBusiness { get; init; }

    /// <summary>What the pack calls the member's first-level appeal, for error messages.</summary>
    public required string AppealName { get; init; }

    public required TimeSpan AppealCeiling { get; init; }
    public required TimeSpan ExpeditedAppealCeiling { get; init; }
    public required TimeSpan GrievanceCeiling { get; init; }

    /// <summary>
    /// Calendar days the member has to file, as a federal minimum and, where
    /// federal law fixes it exactly, a maximum. A plan or State may give the
    /// member longer unless the rule sets the number outright.
    /// </summary>
    public required (int Min, int? Max) FilingWindowDays { get; init; }

    /// <summary>
    /// The State fair hearing request window federal law requires, or
    /// <c>null</c> where there is no fair hearing at all. Medicare Advantage
    /// has none: an upheld reconsideration goes to the IRE, not to a State.
    /// </summary>
    public required (int Min, int Max)? FairHearingWindowDays { get; init; }

    public required int MaxExtensionDays { get; init; }

    public required string FilingWindowCitation { get; init; }
    public required string AcknowledgmentCitation { get; init; }
    public required string AppealCitation { get; init; }
    public required string ExpeditedAppealCitation { get; init; }
    public required string GrievanceCitation { get; init; }
    public required string ExtensionCitation { get; init; }

    private static TimeSpan Ceiling(LineOfBusiness lob, AppealType type, bool urgent) =>
        AppealResponseDeadlinePolicy.EnforceableMaximumWindow(lob, type, AppealLevel.FirstLevel, urgent)!.Value;

    /// <summary>
    /// 42 CFR Part 438 Subpart F. The appeal ceiling is the pre-service
    /// clock, which is also what <see cref="AppealResponseDeadlinePolicy"/>
    /// enforces.
    /// </summary>
    public static readonly RulePackFloor Medicaid = new()
    {
        LineOfBusiness = LineOfBusiness.Medicaid,
        AppealName = "appeal",
        AppealCeiling = Ceiling(LineOfBusiness.Medicaid, AppealType.Reconsideration, urgent: false),
        ExpeditedAppealCeiling = Ceiling(LineOfBusiness.Medicaid, AppealType.Reconsideration, urgent: true),
        GrievanceCeiling = Ceiling(LineOfBusiness.Medicaid, AppealType.Grievance, urgent: false),
        FilingWindowDays = (60, 60),
        FairHearingWindowDays = (90, 120),
        MaxExtensionDays = (int)AppealResponseDeadlinePolicy.MaxExtension.TotalDays,
        FilingWindowCitation = "42 CFR 438.402(c)(2)(ii)",
        AcknowledgmentCitation = "42 CFR 438.406(b)(1)",
        AppealCitation = "42 CFR 438.408(b)(2)",
        ExpeditedAppealCitation = "42 CFR 438.408(b)(3)",
        GrievanceCitation = "42 CFR 438.408(b)(1)",
        ExtensionCitation = "42 CFR 438.408(c)",
    };

    /// <summary>
    /// Medicare Advantage (Part C), 42 CFR Part 422 Subpart M. National, not
    /// State: CMS sets these clocks and a plan may only better them.
    ///
    /// <para>The appeal ceiling is the <b>pre-service</b> reconsideration
    /// clock (30 days, 422.590(a)). MA payment reconsiderations get 60 days
    /// (422.590(b)), but neither the appeal model nor
    /// <see cref="AppealResponseDeadlinePolicy"/> distinguishes pre-service
    /// from payment, and both have enforced the shorter clock since they
    /// were written. A pack that claimed 60 would disagree with the policy
    /// the service actually applies, so the pack records the payment clock
    /// as a note and is validated against the conservative one.</para>
    ///
    /// <para>The 24-hour expedited grievance clock (422.564(f)) covers only
    /// grievances about an extension or a refusal to expedite, which the
    /// model cannot tell from any other grievance. It is in the pack as the
    /// expedited resolution, and measured against the 30-day ceiling, for
    /// the same reason.</para>
    /// </summary>
    public static readonly RulePackFloor MedicareAdvantage = new()
    {
        LineOfBusiness = LineOfBusiness.Medicare,
        AppealName = "reconsideration",
        AppealCeiling = Ceiling(LineOfBusiness.Medicare, AppealType.Reconsideration, urgent: false),
        ExpeditedAppealCeiling = Ceiling(LineOfBusiness.Medicare, AppealType.Reconsideration, urgent: true),
        GrievanceCeiling = Ceiling(LineOfBusiness.Medicare, AppealType.Grievance, urgent: false),
        // 422.582(b) gives the enrollee 60 days from the notice; a plan may
        // allow longer, and 422.582(c) lets it accept a late filing for good
        // cause, so only the minimum is fixed.
        FilingWindowDays = (60, null),
        FairHearingWindowDays = null,
        MaxExtensionDays = (int)AppealResponseDeadlinePolicy.MaxExtension.TotalDays,
        FilingWindowCitation = "42 CFR 422.582(b)",
        AcknowledgmentCitation = "42 CFR 422.568(a)",
        AppealCitation = "42 CFR 422.590(a)",
        ExpeditedAppealCitation = "42 CFR 422.590(d)(1)",
        GrievanceCitation = "42 CFR 422.564(e)(1)",
        ExtensionCitation = "42 CFR 422.590(f)",
    };

    public static readonly IReadOnlyList<RulePackFloor> All = [Medicaid, MedicareAdvantage];

    /// <summary>The floor for a line of business, or <c>null</c> where none is defined yet.</summary>
    public static RulePackFloor? For(LineOfBusiness lineOfBusiness) =>
        All.FirstOrDefault(f => f.LineOfBusiness == lineOfBusiness);

    /// <summary>Lines of business a pack may be written for, for the error message that refuses the others.</summary>
    public static string SupportedNames => string.Join(", ", All.Select(f => f.LineOfBusiness));
}
