using CloudHealthOffice.FeeScheduleEngine.Models;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// Works out what a write to the fee schedule store changed: which
/// schedule-level values moved and which rate lines were added, removed,
/// raised, lowered or otherwise changed. Pure, so it is tested without a
/// database; the store calls it on every create and replace.
/// </summary>
public static class FeeScheduleChangeDetector
{
    /// <summary>
    /// Past this many changed codes the change is recorded as
    /// <see cref="FeeScheduleChange.AllCodes"/> instead of listing them, which
    /// keeps the record well under MongoDB's document limit. A finder then
    /// treats every line priced on the schedule as a candidate, which is safe.
    /// </summary>
    public const int MaxListedCodes = 5000;

    public static FeeScheduleChange Compare(FeeSchedule? before, FeeSchedule after, DateTime changedAt)
    {
        var change = new FeeScheduleChange
        {
            Id = FeeScheduleChange.MakeId(after.Id, after.Revision),
            TenantId = after.TenantId,
            FeeScheduleId = after.Id,
            FeeScheduleName = after.Name,
            FeeScheduleType = after.Type,
            Kind = before is null ? FeeScheduleChangeKind.Created : FeeScheduleChangeKind.Replaced,
            PreviousRevision = before?.Revision,
            NewRevision = after.Revision,
            EffectiveDate = after.EffectiveDate,
            TermDate = after.TermDate,
            DefaultForPlanId = after.DefaultForPlanId,
            SupplementalForPlanIds = after.SupplementalForPlanIds?.ToList() ?? new(),
            ChangedAt = changedAt,
        };

        if (before is null)
        {
            // Every line is new; listing them adds nothing a finder needs.
            change.LinesAdded = after.Lines.Count;
            return change;
        }

        if (before.EffectiveDate != after.EffectiveDate || before.TermDate != after.TermDate)
        {
            change.PreviousEffectiveDate = before.EffectiveDate;
            change.PreviousTermDate = before.TermDate;
        }

        change.ScheduleLevelChanges = ScheduleLevelChanges(before, after);

        var beforeLines = Index(before.Lines);
        var afterLines = Index(after.Lines);
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (key, newLine) in afterLines)
        {
            if (!beforeLines.TryGetValue(key, out var oldLine))
            {
                change.LinesAdded++;
                codes.Add(CodeOf(newLine));
                continue;
            }

            if (SameLine(oldLine, newLine))
                continue;

            codes.Add(CodeOf(newLine));
            if (oldLine.RateType == newLine.RateType && OnlyRatesMoved(oldLine, newLine))
            {
                var direction = Direction(oldLine.Rate, newLine.Rate) + Direction(oldLine.FacilityRate, newLine.FacilityRate);
                if (direction > 0) change.LinesRateIncreased++;
                else if (direction < 0) change.LinesRateDecreased++;
                else change.LinesOtherwiseChanged++;
            }
            else
            {
                change.LinesOtherwiseChanged++;
            }
        }

        foreach (var (key, oldLine) in beforeLines)
        {
            if (afterLines.ContainsKey(key)) continue;
            change.LinesRemoved++;
            codes.Add(CodeOf(oldLine));
        }

        change.AllCodes = change.ScheduleLevelChanges.Count > 0 || codes.Count > MaxListedCodes;
        if (!change.AllCodes)
            change.ChangedCodes = codes.ToList();

        return change;
    }

    private static List<string> ScheduleLevelChanges(FeeSchedule b, FeeSchedule a)
    {
        var changes = new List<string>();
        void Check<T>(string name, T before, T after)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after))
                changes.Add($"{name}: {Show(before)} -> {Show(after)}");
        }

        Check("effective date", b.EffectiveDate.ToString("yyyy-MM-dd"), a.EffectiveDate.ToString("yyyy-MM-dd"));
        Check("term date", b.TermDate?.ToString("yyyy-MM-dd"), a.TermDate?.ToString("yyyy-MM-dd"));
        Check("type", b.Type, a.Type);
        Check("conversion factor", b.ConversionFactor, a.ConversionFactor);
        Check("work GPCI", b.WorkGpci, a.WorkGpci);
        Check("PE GPCI", b.PeGpci, a.PeGpci);
        Check("MP GPCI", b.MpGpci, a.MpGpci);
        Check("percent of Medicare", b.PercentOfMedicare, a.PercentOfMedicare);
        Check("Medicare reference schedule", b.BaseMpfsFeeScheduleId, a.BaseMpfsFeeScheduleId);
        Check("per diem rate", b.PerDiemRate, a.PerDiemRate);
        Check("DRG base rate", b.DrgBaseRate, a.DrgBaseRate);
        Check("default for plan", b.DefaultForPlanId, a.DefaultForPlanId);
        Check("supplemental for plans",
            string.Join(",", (b.SupplementalForPlanIds ?? new()).Order(StringComparer.Ordinal)),
            string.Join(",", (a.SupplementalForPlanIds ?? new()).Order(StringComparer.Ordinal)));
        Check("claims runout months", b.ClaimsRunoutMonths, a.ClaimsRunoutMonths);
        return changes;
    }

    private static string Show<T>(T value) => value is null ? "(none)" : value.ToString() ?? "(none)";

    /// <summary>
    /// Lines keyed the way the engine looks them up: procedure code, modifier
    /// and revenue code. A schedule with a repeated key keeps the first, which
    /// is the one <c>ResolveLinePriority</c> would find.
    /// </summary>
    private static Dictionary<string, FeeScheduleLine> Index(IEnumerable<FeeScheduleLine> lines)
    {
        var index = new Dictionary<string, FeeScheduleLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            index.TryAdd(Key(line), line);
        return index;
    }

    private static string Key(FeeScheduleLine l) =>
        $"{l.ProcedureCode?.Trim()}|{l.Modifier?.Trim()}|{l.RevenueCode?.Trim()}";

    private static string CodeOf(FeeScheduleLine l) =>
        !string.IsNullOrWhiteSpace(l.ProcedureCode)
            ? l.ProcedureCode.Trim().ToUpperInvariant()
            : $"rev:{l.RevenueCode?.Trim()}";

    private static bool SameLine(FeeScheduleLine a, FeeScheduleLine b) =>
        a.RateType == b.RateType
        && a.Rate == b.Rate
        && a.FacilityRate == b.FacilityRate
        && OnlyRatesMoved(a, b);

    /// <summary>True when everything but <c>Rate</c> / <c>FacilityRate</c> is the same.</summary>
    private static bool OnlyRatesMoved(FeeScheduleLine a, FeeScheduleLine b) =>
        a.WorkRvu == b.WorkRvu
        && a.PeRvu == b.PeRvu
        && a.MpRvu == b.MpRvu
        && a.PeRvuFacility == b.PeRvuFacility
        && a.DrgWeight == b.DrgWeight
        && a.MaxUnitsPerDay == b.MaxUnitsPerDay
        && a.BilateralAdjustmentApplies == b.BilateralAdjustmentApplies
        && a.MultipleProcedureIndicator == b.MultipleProcedureIndicator
        && a.AssistantAtSurgeryAllowed == b.AssistantAtSurgeryAllowed;

    private static int Direction(decimal? before, decimal? after) =>
        (before, after) switch
        {
            (null, null) => 0,
            ({ } x, { } y) => Math.Sign(y - x),
            _ => 0,
        };
}
