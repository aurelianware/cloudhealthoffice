using ClaimsService.Models;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Decides whether a paid claim is touched by a fee schedule change, and how
/// it was tied to it. Pure, so it is tested without a database.
/// </summary>
public static class RetroRateMatcher
{
    /// <summary>The claim's tie to the change, or null when the change does not touch it.</summary>
    public sealed record MatchResult(string Match, IReadOnlyList<int> AffectedLines, long? PricedOnRevision);

    /// <summary>
    /// The service-date window the change can reach: from the earliest
    /// effective date (before or after the change) to the latest term date
    /// (null when either is open-ended).
    /// </summary>
    public static (DateTime From, DateTime? Before) ServiceWindow(FeeScheduleChange change)
    {
        var from = change.PreviousEffectiveDate is { } prev && prev < change.EffectiveDate
            ? prev
            : change.EffectiveDate;
        DateTime? before = change.TermDate;
        if (change.PreviousEffectiveDate is not null)
        {
            // The range moved: reach the later of the two ends; open if either is open.
            before = change.TermDate is null || change.PreviousTermDate is null
                ? null
                : (change.TermDate > change.PreviousTermDate ? change.TermDate : change.PreviousTermDate);
        }
        return (from, before);
    }

    /// <summary>The plans whose claims may have used the schedule without recording it.</summary>
    public static IReadOnlyList<string> PlanKeys(FeeScheduleChange change) =>
        new[] { change.DefaultForPlanId }
            .Concat(change.SupplementalForPlanIds ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static MatchResult? Match(Claim claim, FeeScheduleChange change)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(change);

        var (from, before) = ServiceWindow(change);
        var codes = new HashSet<string>(change.ChangedCodes ?? [], StringComparer.OrdinalIgnoreCase);
        var result = claim.AdjudicationResult;
        var pricing = result?.LinePricing ?? [];

        bool CodeChanged(int lineNumber, string? procedureCode)
        {
            if (change.AllCodes) return true;
            if (!string.IsNullOrWhiteSpace(procedureCode) && codes.Contains(procedureCode.Trim())) return true;
            var revenue = claim.ClaimLines.FirstOrDefault(l => l.LineNumber == lineNumber)?.RevenueCode;
            return !string.IsNullOrWhiteSpace(revenue) && codes.Contains($"rev:{revenue.Trim()}");
        }

        bool InWindow(DateTime serviceDate) =>
            serviceDate.Date >= from.Date && (before is null || serviceDate.Date < before.Value.Date);

        if (change.Kind == FeeScheduleChangeKind.Replaced)
        {
            // 1. Lines that record this schedule at a revision older than the change.
            var priced = pricing
                .Where(p => string.Equals(p.FeeScheduleId, change.FeeScheduleId, StringComparison.Ordinal)
                            && (p.FeeScheduleRevision is null || p.FeeScheduleRevision < change.NewRevision)
                            && InWindow(p.ServiceDate == default ? claim.ServiceDateFrom : p.ServiceDate)
                            && CodeChanged(p.LineNumber, p.ProcedureCode))
                .ToList();
            if (priced.Count > 0)
            {
                return new MatchResult(
                    RetroRateMatch.Provenance,
                    priced.Select(p => p.LineNumber).Distinct().Order().ToList(),
                    priced.Select(p => p.FeeScheduleRevision).Where(r => r is not null).Min());
            }

            // 2. A supplemental payment from this schedule on a changed code.
            var supplemental = (result?.SupplementalPayments ?? [])
                .Where(s => string.Equals(s.FeeScheduleId, change.FeeScheduleId, StringComparison.Ordinal)
                            && CodeChanged(s.LineNumber, s.ProcedureCode))
                .Select(s => s.LineNumber)
                .Distinct()
                .Order()
                .ToList();
            if (supplemental.Count > 0 && InWindow(claim.ServiceDateFrom))
                return new MatchResult(RetroRateMatch.Supplemental, supplemental, null);
        }

        // 3. No provenance at all: fall back to the plan, the dates and the codes.
        var planKeys = PlanKeys(change);
        var onPlan = !string.IsNullOrWhiteSpace(claim.BenefitPlanId)
                     && planKeys.Contains(claim.BenefitPlanId.Trim(), StringComparer.Ordinal);
        if (!onPlan) return null;

        if (change.Kind == FeeScheduleChangeKind.Created)
        {
            // A new schedule for the plan, effective back over claims already
            // priced on something else. Only claims not already priced on it.
            if (pricing.Any(p => string.Equals(p.FeeScheduleId, change.FeeScheduleId, StringComparison.Ordinal)))
                return null;
            var lines = claim.ClaimLines
                .Where(l => InWindow(LineDate(claim, l)))
                .Select(l => l.LineNumber)
                .Order()
                .ToList();
            return lines.Count == 0 ? null : new MatchResult(RetroRateMatch.NewSchedule, lines, null);
        }

        if (pricing.Count > 0) return null; // provenance says another schedule priced it

        var fallback = claim.ClaimLines
            .Where(l => InWindow(LineDate(claim, l)) && CodeChanged(l.LineNumber, l.ProcedureCode))
            .Select(l => l.LineNumber)
            .Order()
            .ToList();
        return fallback.Count == 0 ? null : new MatchResult(RetroRateMatch.PlanFallback, fallback, null);
    }

    private static DateTime LineDate(Claim claim, ClaimLine line) =>
        line.ServiceDateFrom != default ? line.ServiceDateFrom : claim.ServiceDateFrom;

    /// <summary>Classifies a re-priced claim by its paid difference.</summary>
    public static string Direction(bool decided, decimal netPaidDelta, decimal tolerance) =>
        !decided ? RetroRateDirection.Undecided
        : netPaidDelta > tolerance ? RetroRateDirection.Underpaid
        : netPaidDelta < -tolerance ? RetroRateDirection.Overpaid
        : RetroRateDirection.Unchanged;
}
