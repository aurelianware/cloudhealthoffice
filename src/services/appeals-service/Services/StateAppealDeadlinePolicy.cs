using AppealsService.Models;
using AppealsService.Services.RulePacks;

namespace AppealsService.Services;

/// <summary>
/// The deadlines one appeal or grievance runs on: the federal clocks of
/// <see cref="AppealResponseDeadlinePolicy"/>, tightened by the State's
/// <see cref="RulePack"/> when the case names a State that has one.
/// </summary>
/// <param name="DefaultTarget">Target response instant used when the caller supplies none.</param>
/// <param name="EnforceableMaximum">
/// Latest permissible response instant, or <c>null</c> when no ceiling
/// applies (commercial grievances). A caller date past it is rejected.
/// </param>
/// <param name="Extension">The one-time extension the plan may take.</param>
/// <param name="RulePack">The State pack applied, as <c>id:version</c>; <c>null</c> when federal rules alone apply.</param>
public sealed record AppealDeadlines(
    DateTime DefaultTarget,
    DateTime? EnforceableMaximum,
    AppealExtensionRule Extension,
    string? RulePack)
{
    /// <summary>
    /// Latest instant an extended deadline may reach: the enforceable
    /// maximum plus the permitted extension, or <c>null</c> when no ceiling
    /// applies.
    /// </summary>
    public DateTime? MaxExtendedTarget => EnforceableMaximum + Extension.MaxExtension;
}

/// <summary>
/// Applies a State rule pack's grievance clocks on top of the federal
/// deadline policy. Pure: no clock access, no I/O.
///
/// <para>A State can tighten a federal clock and never loosen it, the same
/// rule <see cref="RulePackValidator"/> enforces on the packs themselves:</para>
/// <list type="bullet">
///   <item>Default target = the earlier of the federal default and the
///   pack's grievance resolution clock. A Medi-Cal grievance gets 30 days
///   (28 CCR 1300.68) instead of the federal 90, and 72 hours when
///   expedited. A Texas expedited grievance keeps the federal 72-hour
///   default because the Texas pack sets no expedited grievance clock.</item>
///   <item>Enforceable maximum = the earlier of the federal ceiling and the
///   pack's clock, so a caller date past State law is rejected.</item>
///   <item>Extension = the pack's grievance extension rule when it states
///   one: none for a Medi-Cal grievance (APL 21-011). A pack that is silent
///   on grievance extensions leaves the federal rule in place, and a pack
///   can never permit an extension that federal rules refuse.</item>
/// </list>
///
/// <para><b>Grievances only, for now.</b> Appeals stay on the federal
/// clocks. The appeal model cannot yet tell a member's appeal from a
/// provider's claim dispute (the case "track",
/// docs/architecture/appeals-case-model.md §8 step 1), and a State's member
/// appeal clock (Colorado's 10 working days, Illinois's 15 business days)
/// must not reach a provider claim reconsideration. Grievances carry no
/// ambiguity beyond what the federal policy already assumes: it gives every
/// Medicaid grievance the member grievance clock of 42 CFR 438.408(b)(1).
/// For California and Texas the member appeal packs equal the federal
/// clocks anyway.</para>
///
/// <para>Only State packs are applied: the national Medicare Advantage pack
/// restates the federal clocks, except its 24-hour expedited grievance
/// clock, which <see cref="AppealResponseDeadlinePolicy"/> deliberately
/// treats as a default rather than a ceiling.</para>
///
/// <para>Grievance variants (for example Ohio's 2-business-day access
/// grievances) are not applied: the appeal model has no field that says
/// which variant a grievance is. The pack's general resolution clock is used.</para>
/// </summary>
public static class StateAppealDeadlinePolicy
{
    public static AppealDeadlines Resolve(
        RulePack? pack,
        DateTime receivedAt,
        AppealCoverage coverage,
        AppealType appealType,
        AppealLevel appealLevel,
        bool isUrgent,
        IHolidayCalendar? holidays = null)
    {
        var federalDefault = AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
            receivedAt, coverage, appealType, appealLevel, isUrgent);
        var federalMaximum = AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(
            receivedAt, coverage, appealType, appealLevel, isUrgent, holidays);
        var federalExtension = AppealResponseDeadlinePolicy.GetExtensionRule(
            coverage, appealType, appealLevel, isUrgent);

        if (!Applies(pack, coverage, appealType, appealLevel))
            return new AppealDeadlines(federalDefault, federalMaximum, federalExtension, null);

        var evaluation = RulePackEvaluator.Evaluate(pack!, new RulePackEvaluationInput
        {
            CaseType = RulePackCaseType.MemberGrievance,
            Expedited = isUrgent,
            ReceivedAt = DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc)
        }, holidays);

        var stateDue = evaluation.Deadlines.FirstOrDefault(d => d.Name == "resolution")?.DueAt;
        if (stateDue is null)
            return new AppealDeadlines(federalDefault, federalMaximum, federalExtension, null);

        var due = DateTime.SpecifyKind(stateDue.Value, receivedAt.Kind);
        var defaultTarget = Earlier(federalDefault, due);
        var maximum = federalMaximum is { } federal ? Earlier(federal, due) : due;

        var extension = StateExtension(pack!, pack!.MemberGrievance.Extension, federalExtension);

        return new AppealDeadlines(defaultTarget, maximum, extension, $"{pack.Id}:{pack.Version}");
    }

    /// <summary>
    /// True when <paramref name="pack"/> governs a case of this kind: a
    /// grievance under a State pack of the same line of business. A Part D
    /// case is never governed by a Medicare Advantage pack.
    /// </summary>
    public static bool Applies(RulePack? pack, AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel) =>
        pack is not null
        && !pack.IsNational
        && !coverage.IsPartD
        && pack.LineOfBusiness == coverage.LineOfBusiness
        && appealType == AppealType.Grievance
        && !AppealResponseDeadlinePolicy.IsExternalReview(appealType, appealLevel);

    private static AppealExtensionRule StateExtension(RulePack pack, ExtensionRule? rules, AppealExtensionRule federal)
    {
        // Silent pack, or a federal refusal a State cannot overturn.
        if (rules is null || !federal.IsPermitted) return federal;

        var citation = pack.Sources.TryGetValue(rules.Source, out var source) ? source.Citation : rules.Source;
        var basis = $"{citation} ({pack.Jurisdiction} rule pack {pack.Version})";

        if (rules.MaxDays <= 0)
            return new AppealExtensionRule(TimeSpan.Zero, $"{basis}: no extension is allowed.");

        var days = TimeSpan.FromDays(rules.MaxDays);
        if (days > federal.MaxExtension) days = federal.MaxExtension;
        var note = rules.RequiresStateApproval ? " Requires State approval before the deadline." : string.Empty;
        return new AppealExtensionRule(days, basis + note);
    }

    private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;
}
