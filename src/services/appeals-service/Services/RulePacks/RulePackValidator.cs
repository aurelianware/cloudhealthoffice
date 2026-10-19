using AppealsService.Models;

namespace AppealsService.Services.RulePacks;

/// <summary>
/// Checks a <see cref="RulePack"/> against the federal floor before it can be
/// used: a State may tighten a federal clock but never loosen it, and every
/// value must cite a source. The ceilings come from
/// <see cref="AppealResponseDeadlinePolicy"/>, so the existing tested policy
/// stays the floor every pack is measured against.
///
/// Business- and working-day clocks have no fixed calendar length, so they
/// are checked by evaluating them from every day of a two-year sweep under
/// the U.S. federal holiday calendar. The evaluator also clamps at run time
/// (<see cref="RulePackEvaluator"/>), so a tenant calendar with extra
/// holidays can never push a deadline past the federal ceiling.
/// </summary>
public static class RulePackValidator
{
    /// <summary>42 CFR 438.402(c)(2)(ii): 60 calendar days to file a Medicaid appeal.</summary>
    public const int MedicaidFilingWindowDays = 60;

    /// <summary>42 CFR 438.408(f)(2): the State sets 90 to 120 days to request a fair hearing.</summary>
    public const int FairHearingWindowMinDays = 90;
    public const int FairHearingWindowMaxDays = 120;

    private static readonly DateTime SweepStart = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const int SweepDays = 730;

    public static TimeSpan MedicaidAppealCeiling =>
        AppealResponseDeadlinePolicy.EnforceableMaximumWindow(
            LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: false)!.Value;

    public static TimeSpan MedicaidExpeditedAppealCeiling =>
        AppealResponseDeadlinePolicy.EnforceableMaximumWindow(
            LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: true)!.Value;

    public static TimeSpan MedicaidGrievanceCeiling =>
        AppealResponseDeadlinePolicy.EnforceableMaximumWindow(
            LineOfBusiness.Medicaid, AppealType.Grievance, AppealLevel.FirstLevel, isUrgent: false)!.Value;

    /// <summary>Every problem with <paramref name="pack"/>; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(RulePack pack)
    {
        var errors = new List<string>();
        void Error(string message) => errors.Add($"{(string.IsNullOrEmpty(pack.Id) ? "(no id)" : pack.Id)}: {message}");

        if (string.IsNullOrWhiteSpace(pack.Id)) Error("id is required.");
        if (pack.State.Length != 2 || !pack.State.All(char.IsAsciiLetterUpper))
            Error($"state '{pack.State}' must be a two-letter upper-case code.");
        if (pack.LineOfBusiness != LineOfBusiness.Medicaid)
            Error($"lineOfBusiness {pack.LineOfBusiness} has no federal floor defined here yet; only Medicaid packs are supported.");
        if (string.IsNullOrWhiteSpace(pack.Version)) Error("version is required.");
        if (pack.EffectiveTo is { } to && to < pack.EffectiveFrom) Error("effectiveTo is before effectiveFrom.");

        foreach (var (name, clock) in AllClocks(pack))
        {
            if (clock.Amount <= 0) Error($"{name}: amount must be positive.");
            if (string.IsNullOrWhiteSpace(clock.Source)) Error($"{name}: source is required.");
            else if (!pack.Sources.ContainsKey(clock.Source)) Error($"{name}: source '{clock.Source}' is not in sources.");
        }
        foreach (var (name, source) in OtherSourceRefs(pack))
        {
            if (string.IsNullOrWhiteSpace(source)) Error($"{name}: source is required.");
            else if (!pack.Sources.ContainsKey(source)) Error($"{name}: source '{source}' is not in sources.");
        }

        var appeal = pack.MemberAppeal;
        if (appeal.FilingWindow.Unit != ClockUnit.CalendarDays || appeal.FilingWindow.Amount != MedicaidFilingWindowDays)
            Error($"memberAppeal.filingWindow must be {MedicaidFilingWindowDays} calendar days (42 CFR 438.402(c)(2)(ii)).");

        if (appeal.FairHearingRequestWindow.Unit != ClockUnit.CalendarDays
            || appeal.FairHearingRequestWindow.Amount is < FairHearingWindowMinDays or > FairHearingWindowMaxDays)
        {
            Error($"memberAppeal.fairHearingRequestWindow must be {FairHearingWindowMinDays}–{FairHearingWindowMaxDays} calendar days (42 CFR 438.408(f)(2)).");
        }

        RequireWithin("memberAppeal.resolution", appeal.Resolution, MedicaidAppealCeiling, Error);
        RequireWithin("memberAppeal.resolutionExpedited", appeal.ResolutionExpedited, MedicaidExpeditedAppealCeiling, Error);
        RequireExtension("memberAppeal.extension", appeal.Extension, Error);

        var grievance = pack.MemberGrievance;
        RequireWithin("memberGrievance.resolution", grievance.Resolution, MedicaidGrievanceCeiling, Error);
        if (grievance.ResolutionExpedited is { } expedited)
            RequireWithin("memberGrievance.resolutionExpedited", expedited, MedicaidGrievanceCeiling, Error);
        foreach (var variant in grievance.Variants)
            RequireWithin($"memberGrievance.variants[{variant.Name}]", variant.Clock, MedicaidGrievanceCeiling, Error);
        if (grievance.Extension is { } grievanceExtension)
            RequireExtension("memberGrievance.extension", grievanceExtension, Error);

        foreach (var (name, clock) in MemberDecisionClocks(pack))
        {
            if (clock.Unit == ClockUnit.Months) Error($"{name}: months are allowed only for filing windows and lookbacks.");
        }

        if (pack.Recoupment.Count == 0) Error("recoupment needs at least one entry (use hold 'unknown' when not determined).");
        if (pack.Recoupment.GroupBy(r => r.EffectiveFrom).Any(g => g.Count() > 1))
            Error("recoupment has two entries with the same effectiveFrom.");

        return errors;
    }

    private static void RequireWithin(string name, Clock clock, TimeSpan ceiling, Action<string> error)
    {
        if (clock.Amount <= 0) return; // reported already
        for (var day = 0; day < SweepDays; day++)
        {
            var start = SweepStart.AddDays(day);
            var due = RulePackClock.Due(start, clock, UsFederalHolidayCalendar.Instance);
            if (due > start + ceiling)
            {
                error($"{name} ({clock}) can fall later than the federal ceiling of {Describe(ceiling)} " +
                      $"(e.g. from {start:yyyy-MM-dd}: due {due:yyyy-MM-dd HH:mm}).");
                return;
            }
        }
    }

    private static void RequireExtension(string name, ExtensionRule extension, Action<string> error)
    {
        if (extension.MaxDays < 0) error($"{name}.maxDays must not be negative.");
        if (extension.MaxDays > AppealResponseDeadlinePolicy.MaxExtension.TotalDays)
            error($"{name}.maxDays {extension.MaxDays} exceeds the federal {AppealResponseDeadlinePolicy.MaxExtension.TotalDays} days (42 CFR 438.408(c)).");
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours < 24 * 4 ? $"{span.TotalHours:0} hours" : $"{span.TotalDays:0} days";

    private static IEnumerable<(string Name, Clock Clock)> MemberDecisionClocks(RulePack pack)
    {
        var a = pack.MemberAppeal;
        yield return ("memberAppeal.resolution", a.Resolution);
        yield return ("memberAppeal.resolutionExpedited", a.ResolutionExpedited);
        if (a.Acknowledgment is { } ack) yield return ("memberAppeal.acknowledgment", ack);
        if (a.AcknowledgmentExpedited is { } ackX) yield return ("memberAppeal.acknowledgmentExpedited", ackX);
        var g = pack.MemberGrievance;
        yield return ("memberGrievance.resolution", g.Resolution);
        if (g.Acknowledgment is { } gAck) yield return ("memberGrievance.acknowledgment", gAck);
        if (g.ResolutionExpedited is { } gX) yield return ("memberGrievance.resolutionExpedited", gX);
    }

    /// <summary>Every clock in the pack, named by its path.</summary>
    internal static IEnumerable<(string Name, Clock Clock)> AllClocks(RulePack pack)
    {
        var a = pack.MemberAppeal;
        yield return ("memberAppeal.filingWindow", a.FilingWindow);
        yield return ("memberAppeal.resolution", a.Resolution);
        yield return ("memberAppeal.resolutionExpedited", a.ResolutionExpedited);
        yield return ("memberAppeal.fairHearingRequestWindow", a.FairHearingRequestWindow);
        if (a.Acknowledgment is { } ack) yield return ("memberAppeal.acknowledgment", ack);
        if (a.AcknowledgmentExpedited is { } ackX) yield return ("memberAppeal.acknowledgmentExpedited", ackX);
        if (a.ContinuationRequestWindow is { } cont) yield return ("memberAppeal.continuationRequestWindow", cont);
        if (a.Extension.WrittenNotice is { } notice) yield return ("memberAppeal.extension.writtenNotice", notice);
        foreach (var (step, i) in a.Chain.Select((s, i) => (s, i)))
            foreach (var c in StepClocks($"memberAppeal.chain[{i}]", step)) yield return c;

        var g = pack.MemberGrievance;
        yield return ("memberGrievance.resolution", g.Resolution);
        if (g.Acknowledgment is { } gAck) yield return ("memberGrievance.acknowledgment", gAck);
        if (g.ResolutionExpedited is { } gX) yield return ("memberGrievance.resolutionExpedited", gX);
        foreach (var v in g.Variants) yield return ($"memberGrievance.variants[{v.Name}]", v.Clock);
        if (g.Extension?.WrittenNotice is { } gNotice) yield return ("memberGrievance.extension.writtenNotice", gNotice);

        var p = pack.ProviderDispute;
        foreach (var (step, i) in p.Chain.Select((s, i) => (s, i)))
            foreach (var c in StepClocks($"providerDispute.chain[{i}]", step)) yield return c;
        foreach (var c in p.Clocks) yield return ($"providerDispute.clocks[{c.Name}]", c.Clock);
    }

    private static IEnumerable<(string, Clock)> StepClocks(string prefix, LevelStep step)
    {
        if (step.RequestWindow is { } w) yield return ($"{prefix}.requestWindow", w);
        if (step.Decision is { } d) yield return ($"{prefix}.decision", d);
    }

    private static IEnumerable<(string Name, string Source)> OtherSourceRefs(RulePack pack)
    {
        yield return ("memberAppeal.extension", pack.MemberAppeal.Extension.Source);
        if (pack.MemberGrievance.Extension is { } ge) yield return ("memberGrievance.extension", ge.Source);
        foreach (var (r, i) in pack.Recoupment.Select((r, i) => (r, i))) yield return ($"recoupment[{i}]", r.Source);
        foreach (var (s, i) in pack.MemberAppeal.Chain.Select((s, i) => (s, i)).Where(x => x.s.Source is not null))
            yield return ($"memberAppeal.chain[{i}]", s.Source!);
        foreach (var (s, i) in pack.ProviderDispute.Chain.Select((s, i) => (s, i)).Where(x => x.s.Source is not null))
            yield return ($"providerDispute.chain[{i}]", s.Source!);
        foreach (var (c, i) in pack.Conflicts.Select((c, i) => (c, i)))
            foreach (var src in c.Sources) yield return ($"conflicts[{i}]", src);
    }
}
