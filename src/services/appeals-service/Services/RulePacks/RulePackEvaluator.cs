using AppealsService.Models;

namespace AppealsService.Services.RulePacks;

public enum RulePackCaseType
{
    MemberAppeal,
    MemberGrievance
}

/// <summary>What to evaluate. All instants are UTC.</summary>
public sealed record RulePackEvaluationInput
{
    public RulePackCaseType CaseType { get; init; } = RulePackCaseType.MemberAppeal;
    public bool Expedited { get; init; }
    public DateTime ReceivedAt { get; init; }

    /// <summary>When the information needed to decide arrived; clocks that run from it (Illinois expedited) use it.</summary>
    public DateTime? InformationReceivedAt { get; init; }

    /// <summary>When the plan decided; gives the fair hearing request deadline.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>When the adverse benefit determination notice was sent; gives the continuation request deadline.</summary>
    public DateTime? NoticeSentAt { get; init; }
}

/// <summary>One computed deadline and the rule behind it.</summary>
public sealed record RuleDeadline(
    string Name,
    DateTime? DueAt,
    string? Rule,
    string? Citation,
    string? Url,
    bool ClampedToFederalCeiling = false,
    DateTime? FederalCeilingAt = null,
    string? Note = null);

public sealed record RulePackEvaluation(
    string PackId,
    string State,
    LineOfBusiness LineOfBusiness,
    string Version,
    DateOnly VerifiedOn,
    RulePackCaseType CaseType,
    bool Expedited,
    IReadOnlyList<RuleDeadline> Deadlines,
    IReadOnlyList<string> ToConfirm);

/// <summary>
/// Computes deadlines from a <see cref="RulePack"/>. Every decision deadline
/// is also measured against the federal ceiling from receipt and clamped to
/// it, so neither a pack value nor a tenant holiday calendar can produce a
/// date later than federal law allows; a clamped deadline says so.
/// </summary>
public static class RulePackEvaluator
{
    /// <summary>
    /// The pack's own federal floor. Every pack in the catalog has one
    /// (the validator refuses a line of business without), so the fallback
    /// is only for a pack built in a test.
    /// </summary>
    private static RulePackFloor Floor(RulePack pack) =>
        RulePackFloor.For(pack.LineOfBusiness) ?? RulePackFloor.Medicaid;

    public static RulePackEvaluation Evaluate(RulePack pack, RulePackEvaluationInput input, IHolidayCalendar? holidays = null)
    {
        if (input.ReceivedAt.Kind == DateTimeKind.Local)
            throw new ArgumentException("receivedAt must be UTC or unspecified, not local time.", nameof(input));

        var deadlines = input.CaseType switch
        {
            RulePackCaseType.MemberAppeal => MemberAppeal(pack, input, holidays),
            RulePackCaseType.MemberGrievance => MemberGrievance(pack, input, holidays),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.CaseType, "Unknown case type.")
        };

        return new RulePackEvaluation(
            pack.Id, pack.Jurisdiction, pack.LineOfBusiness, pack.Version, pack.VerifiedOn,
            input.CaseType, input.Expedited, deadlines, pack.ToConfirm);
    }

    /// <summary>The recoupment rule in effect on <paramref name="asOf"/>, or <c>null</c>.</summary>
    public static RecoupmentRule? RecoupmentAsOf(RulePack pack, DateOnly asOf) =>
        pack.Recoupment
            .Where(r => r.EffectiveFrom is null || r.EffectiveFrom <= asOf)
            .OrderByDescending(r => r.EffectiveFrom ?? DateOnly.MinValue)
            .FirstOrDefault();

    private static List<RuleDeadline> MemberAppeal(RulePack pack, RulePackEvaluationInput input, IHolidayCalendar? holidays)
    {
        var rules = pack.MemberAppeal;
        var floor = Floor(pack);
        var deadlines = new List<RuleDeadline>();

        var ack = input.Expedited ? rules.AcknowledgmentExpedited : rules.Acknowledgment;
        if (ack is not null)
            deadlines.Add(FromClock(pack, "acknowledgment", input.ReceivedAt, ack, holidays));
        else if (!input.Expedited)
            deadlines.Add(new RuleDeadline("acknowledgment", null, null, floor.AcknowledgmentCitation, null,
                Note: "Acknowledgment is required; the pack sets no deadline."));

        var resolutionClock = input.Expedited ? rules.ResolutionExpedited : rules.Resolution;
        var ceiling = input.ReceivedAt + (input.Expedited ? floor.ExpeditedAppealCeiling : floor.AppealCeiling);
        var resolution = Decision(pack, "resolution", input, resolutionClock, ceiling,
            input.Expedited ? floor.ExpeditedAppealCitation : floor.AppealCitation, holidays);
        deadlines.Add(resolution);

        if (rules.Extension.MaxDays > 0 && resolution.DueAt is { } resolvedBy)
        {
            var extendedCeiling = ceiling + AppealResponseDeadlinePolicy.MaxExtension;
            var extended = resolvedBy.AddDays(rules.Extension.MaxDays);
            var clamped = extended > extendedCeiling;
            var notes = new List<string>();
            if (rules.Extension.Note is { } extensionNote) notes.Add(extensionNote);
            if (rules.Extension.RequiresStateApproval) notes.Add("Requires State approval before the deadline.");
            if (rules.Extension.WrittenNotice is { } notice) notes.Add($"Written notice to the member within {Describe(notice)} of extending.");
            var source = Source(pack, rules.Extension.Source);
            deadlines.Add(new RuleDeadline(
                "resolutionWithExtension",
                clamped ? extendedCeiling : extended,
                $"resolution + {rules.Extension.MaxDays} calendar days",
                source?.Citation, source?.Url,
                clamped, extendedCeiling,
                notes.Count > 0 ? string.Join(" ", notes) : null));
        }
        else if (rules.Extension.MaxDays == 0)
        {
            var source = Source(pack, rules.Extension.Source);
            deadlines.Add(new RuleDeadline("resolutionWithExtension", null, "no extension",
                source?.Citation ?? floor.ExtensionCitation, source?.Url, Note: "No extension is allowed."));
        }

        if (input.DecidedAt is { } decidedAt && rules.FairHearingRequestWindow is { } fairHearing)
            deadlines.Add(FromClock(pack, "fairHearingRequest", decidedAt, fairHearing, holidays));

        if (input.NoticeSentAt is { } noticeSentAt && rules.ContinuationRequestWindow is { } continuation)
        {
            var d = FromClock(pack, "continuationRequest", noticeSentAt, continuation, holidays);
            deadlines.Add(d with { Note = continuation.Note ?? "Or by the action's effective date, whichever is later (42 CFR 438.420(a))." });
        }

        return deadlines;
    }

    private static List<RuleDeadline> MemberGrievance(RulePack pack, RulePackEvaluationInput input, IHolidayCalendar? holidays)
    {
        var rules = pack.MemberGrievance;
        var floor = Floor(pack);
        var deadlines = new List<RuleDeadline>();
        var ceiling = input.ReceivedAt + floor.GrievanceCeiling;

        if (rules.Acknowledgment is { } ack)
            deadlines.Add(FromClock(pack, "acknowledgment", input.ReceivedAt, ack, holidays));
        else
            deadlines.Add(new RuleDeadline("acknowledgment", null, null, floor.AcknowledgmentCitation, null,
                Note: "Acknowledgment is required; the pack sets no deadline."));

        var clock = input.Expedited && rules.ResolutionExpedited is { } expedited ? expedited : rules.Resolution;
        deadlines.Add(Decision(pack, "resolution", input, clock, ceiling, floor.GrievanceCitation, holidays));

        foreach (var variant in rules.Variants)
            deadlines.Add(Decision(pack, $"resolution:{variant.Name}", input, variant.Clock, ceiling,
                floor.GrievanceCitation, holidays));

        return deadlines;
    }

    private static RuleDeadline Decision(
        RulePack pack, string name, RulePackEvaluationInput input, Clock clock, DateTime ceiling, string ceilingCitation,
        IHolidayCalendar? holidays)
    {
        string? note = clock.Note;
        var start = input.ReceivedAt;
        if (clock.From == ClockStart.InformationReceived)
        {
            if (input.InformationReceivedAt is { } info && info > input.ReceivedAt) start = info;
            else if (input.InformationReceivedAt is null)
                note = Join(note, "Runs from when the needed information arrives; not given, so measured from receipt.");
        }

        var due = RulePackClock.Due(start, clock, holidays);
        var source = Source(pack, clock.Source);
        if (due > ceiling)
        {
            return new RuleDeadline(name, ceiling, clock.ToString(), source?.Citation, source?.Url,
                ClampedToFederalCeiling: true, FederalCeilingAt: ceiling,
                Note: Join(note, $"Capped at the federal ceiling ({ceilingCitation})."));
        }
        return new RuleDeadline(name, due, clock.ToString(), source?.Citation, source?.Url, false, ceiling, note);
    }

    private static RuleDeadline FromClock(RulePack pack, string name, DateTime start, Clock clock, IHolidayCalendar? holidays)
    {
        var source = Source(pack, clock.Source);
        return new RuleDeadline(name, RulePackClock.Due(start, clock, holidays), clock.ToString(), source?.Citation, source?.Url, Note: clock.Note);
    }

    private static RuleSource? Source(RulePack pack, string id) =>
        pack.Sources.TryGetValue(id, out var source) ? source : null;

    private static string Describe(Clock clock) => $"{clock.Amount} {clock.Unit}";

    private static string? Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : $"{a} {b}";
}
