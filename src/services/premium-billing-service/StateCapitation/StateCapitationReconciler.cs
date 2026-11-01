using CloudHealthOffice.Infrastructure.Money;
using PremiumBillingService.Models;

namespace PremiumBillingService.StateCapitation;

/// <summary>
/// Expected-versus-received state capitation by member, month and rate cell:
/// the plan's revenue receivable against what the state's 820s actually paid.
///
/// <list type="number">
///   <item>Received: every individual-level 820 line becomes member-month
///   amounts (<see cref="ReadReceived"/>). Lines for the same member-month,
///   from any number of 820s (the original payment, a retro adjustment, a
///   recoupment), are summed.</item>
///   <item>Expected: each enrolled member-month is priced at the state's
///   PMPM for its rate cell on the first of the month (prorated by days when
///   the profile says so).</item>
///   <item>Each member-month gets one <see cref="MemberMonthOutcome"/>, and
///   the totals roll up by month and rate cell.</item>
/// </list>
/// Pure: no I/O, so the same inputs always give the same answer.
/// </summary>
public static class StateCapitationReconciler
{
    public static StateCapitationReconciliation Reconcile(
        StateCapitationProfile profile,
        IEnumerable<ExpectedMemberMonth> expected,
        IEnumerable<StateCapitationRate> rates,
        IEnumerable<ReceivedMemberMonth> received)
    {
        var warnings = new List<string>();
        var rateList = rates.ToList();

        var expectedByKey = new Dictionary<(string, DateOnly), ExpectedMemberMonth>();
        foreach (var e in expected)
        {
            var key = (Normalize(e.MemberId), FirstOfMonth(e.Month));
            if (!expectedByKey.TryAdd(key, e with { Month = key.Item2 }))
                warnings.Add($"Member {e.MemberId} is expected twice for {key.Item2:yyyy-MM}; the first rate cell ({expectedByKey[key].RateCell}) is used");
        }

        var receivedByKey = received
            .GroupBy(r => (Normalize(r.MemberId), FirstOfMonth(r.Month)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var lines = new List<MemberMonthReconciliation>();
        foreach (var key in expectedByKey.Keys.Union(receivedByKey.Keys).OrderBy(k => k.Item2).ThenBy(k => k.Item1, StringComparer.Ordinal))
        {
            expectedByKey.TryGetValue(key, out var exp);
            var got = receivedByKey.TryGetValue(key, out var r) ? r : [];
            lines.Add(Line(profile, key.Item1, key.Item2, exp, got, rateList));
        }

        var summary = lines
            .GroupBy(l => (l.Month, RateCell: l.ExpectedRateCell ?? l.ReceivedRateCell ?? "(none)"))
            .OrderBy(g => g.Key.Month).ThenBy(g => g.Key.RateCell, StringComparer.Ordinal)
            .Select(g => new RateCellMonthSummary(
                g.Key.Month,
                g.Key.RateCell,
                ExpectedMembers: g.Count(l => l.ExpectedRateCell != null),
                PaidMembers: g.Count(l => l.Received > 0),
                Expected: g.Sum(l => l.Expected ?? 0m),
                Received: g.Sum(l => l.Received),
                Variances: g.Count(l => l.Outcome != MemberMonthOutcome.PaidInFull)))
            .ToList();

        if (!profile.Verified)
            warnings.Add($"The {profile.State} 820 profile (rate cell in {profile.RateCellSource} {profile.RateCellQualifier}) has not been checked against the state's companion guide");

        return new StateCapitationReconciliation(profile.State, lines, summary, warnings);
    }

    private static MemberMonthReconciliation Line(
        StateCapitationProfile profile, string memberId, DateOnly month,
        ExpectedMemberMonth? exp, List<ReceivedMemberMonth> got, List<StateCapitationRate> rates)
    {
        var received = MoneyRounding.ToCents(got.Sum(g => g.Amount));
        // The rate cell the state last paid under: a retro line that moves a
        // member to another cell is the state's current view of the month.
        var receivedCell = got.LastOrDefault(g => !string.IsNullOrWhiteSpace(g.RateCell))?.RateCell;
        var traces = got.Select(g => g.TraceNumber).Distinct().ToList();
        var reasons = got.SelectMany(g => g.AdjustmentReasons).Distinct().ToList();

        decimal? expectedAmount = null;
        if (exp != null)
        {
            var rate = rates
                .Where(r => string.Equals(r.RateCell, exp.RateCell, StringComparison.OrdinalIgnoreCase)
                            && r.EffectiveFrom <= month && (r.EffectiveTo == null || r.EffectiveTo >= month))
                .OrderByDescending(r => r.EffectiveFrom)
                .FirstOrDefault();
            if (rate != null)
                expectedAmount = MoneyRounding.ToCents(Prorate(profile, rate.Pmpm, month, exp.DaysEnrolled));
        }

        var outcome = Outcome(profile, exp, expectedAmount, got, received, receivedCell);
        return new MemberMonthReconciliation(memberId, month, exp?.RateCell, receivedCell,
            expectedAmount, received, outcome, traces, reasons);
    }

    private static MemberMonthOutcome Outcome(
        StateCapitationProfile profile, ExpectedMemberMonth? exp, decimal? expectedAmount,
        List<ReceivedMemberMonth> got, decimal received, string? receivedCell)
    {
        if (exp == null)
            return got.Any(g => g.Amount > 0) && received <= 0 ? MemberMonthOutcome.Recouped : MemberMonthOutcome.Unexpected;
        if (got.Count == 0)
            return expectedAmount == null ? MemberMonthOutcome.NoRate : MemberMonthOutcome.NotPaid;
        if (got.Any(g => g.Amount > 0) && received <= 0)
            return MemberMonthOutcome.Recouped;
        if (receivedCell != null && !string.Equals(receivedCell, exp.RateCell, StringComparison.OrdinalIgnoreCase))
            return MemberMonthOutcome.RateCellMismatch;
        if (expectedAmount == null)
            return MemberMonthOutcome.NoRate;
        var variance = received - expectedAmount.Value;
        if (Math.Abs(variance) <= profile.Tolerance)
            return MemberMonthOutcome.PaidInFull;
        return variance < 0 ? MemberMonthOutcome.Underpaid : MemberMonthOutcome.Overpaid;
    }

    private static decimal Prorate(StateCapitationProfile profile, decimal pmpm, DateOnly month, int? days)
    {
        if (!profile.ProrateByDays || days == null)
            return pmpm;
        var inMonth = DateTime.DaysInMonth(month.Year, month.Month);
        return pmpm * Math.Clamp(days.Value, 0, inMonth) / inMonth;
    }

    /// <summary>
    /// The member-month amounts on a set of 820s: each individual-level line
    /// with a member id and a coverage period. A line whose period spans
    /// several months is split evenly across them (cents to the last month)
    /// and noted in <paramref name="warnings"/>; a line with no member or no
    /// period cannot be placed and is noted too.
    /// </summary>
    public static IReadOnlyList<ReceivedMemberMonth> ReadReceived(
        StateCapitationProfile profile, IEnumerable<RemittanceAdvice> advices, ICollection<string> warnings)
    {
        var result = new List<ReceivedMemberMonth>();
        foreach (var advice in advices)
        {
            foreach (var item in advice.Items.Where(i => i.Level == RemittanceLevel.Individual))
            {
                if (string.IsNullOrWhiteSpace(item.MemberId) || item.CoveragePeriodStart == null)
                {
                    warnings.Add($"820 {advice.TraceNumber} line {item.LineNumber}: no member id or coverage period; not placed in a month");
                    continue;
                }

                var cell = RateCell(profile, item);
                var reasons = item.Adjustments.Select(a => a.ReasonCode).OfType<string>().ToList();
                var start = FirstOfMonth(DateOnly.FromDateTime(item.CoveragePeriodStart.Value));
                var end = FirstOfMonth(DateOnly.FromDateTime(item.CoveragePeriodEnd ?? item.CoveragePeriodStart.Value));
                var months = new List<DateOnly>();
                for (var m = start; m <= end; m = m.AddMonths(1))
                    months.Add(m);

                if (months.Count > 1)
                    warnings.Add($"820 {advice.TraceNumber} line {item.LineNumber}: member {item.MemberId} paid for {months.Count} months in one line; split evenly");

                var share = MoneyRounding.ToCents(item.Amount / months.Count);
                for (var i = 0; i < months.Count; i++)
                {
                    var amount = i == months.Count - 1 ? item.Amount - share * (months.Count - 1) : share;
                    result.Add(new ReceivedMemberMonth(item.MemberId!, months[i], cell, amount,
                        advice.TraceNumber, advice.PaymentDate, reasons));
                }
            }
        }
        return result;
    }

    private static string? RateCell(StateCapitationProfile profile, RemittanceItem item) => profile.RateCellSource switch
    {
        RateCellSource.RemittanceReference when string.Equals(item.ReferenceQualifier, profile.RateCellQualifier, StringComparison.OrdinalIgnoreCase)
            => item.Reference,
        RateCellSource.Reference
            => item.References.FirstOrDefault(r => string.Equals(r.Qualifier, profile.RateCellQualifier, StringComparison.OrdinalIgnoreCase))?.Value,
        _ => null,
    };

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static string Normalize(string memberId) => memberId.Trim().ToUpperInvariant();
}
