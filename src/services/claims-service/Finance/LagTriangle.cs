namespace ClaimsService.Finance;

/// <summary>
/// One version of a claim as the lag triangle needs it: when the service was
/// incurred, when (and how much) the plan paid on this version, and whether
/// the version voided the claim.
/// </summary>
public sealed record ClaimPaymentVersion(
    string ClaimNumber,
    int VersionNumber,
    DateTime IncurredDate,
    DateTime? PaidDate,
    decimal PlanPaid,
    bool Voided);

/// <summary>Money the plan paid (or took back) for claims incurred in one month, in one later month.</summary>
public sealed record PaidIncrement(string ClaimNumber, DateOnly IncurredMonth, DateOnly PaidMonth, decimal Amount);

/// <summary>One incurred month of the triangle: cumulative paid at each lag (0 = paid in the incurred month).</summary>
public sealed record LagTriangleRow(DateOnly IncurredMonth, IReadOnlyList<decimal> CumulativePaid)
{
    /// <summary>The last lag observed at the valuation month.</summary>
    public int LatestLag => CumulativePaid.Count - 1;

    public decimal PaidToDate => CumulativePaid.Count == 0 ? 0m : CumulativePaid[^1];
}

/// <summary>Paid claims by incurred month and months of lag, as of a valuation month.</summary>
public sealed record LagTriangle(DateOnly ValuationMonth, IReadOnlyList<LagTriangleRow> Rows, int MaxLag)
{
    /// <summary>
    /// Turns claim versions into paid increments. Each paid version adds what
    /// it paid beyond the claim's previous paid version, in the month it was
    /// paid, so an adjustment adds (or takes back) only the difference and a
    /// void takes back everything paid so far. A claim is placed in the month
    /// of its first version's service date, so an adjustment that moves the
    /// date does not move money between rows.
    /// </summary>
    public static IReadOnlyList<PaidIncrement> Increments(IEnumerable<ClaimPaymentVersion> versions)
    {
        var result = new List<PaidIncrement>();
        foreach (var claim in versions.GroupBy(v => v.ClaimNumber))
        {
            var ordered = claim.OrderBy(v => v.VersionNumber).ToList();
            var incurred = FirstOfMonth(ordered[0].IncurredDate);
            var paidSoFar = 0m;
            foreach (var v in ordered.Where(v => v.PaidDate != null))
            {
                var total = v.Voided ? 0m : v.PlanPaid;
                var delta = total - paidSoFar;
                if (delta != 0m)
                    result.Add(new PaidIncrement(claim.Key, incurred, FirstOfMonth(v.PaidDate!.Value), delta));
                paidSoFar = total;
            }
        }
        return result;
    }

    /// <summary>
    /// The triangle for incurred months <paramref name="fromIncurred"/>..<paramref name="toIncurred"/>
    /// as of <paramref name="valuation"/>: payments after the valuation month
    /// are left out, so a past close can be reproduced. A payment dated before
    /// its incurred month (a data error) is counted at lag 0.
    /// </summary>
    public static LagTriangle Build(IEnumerable<PaidIncrement> increments, DateOnly fromIncurred, DateOnly toIncurred, DateOnly valuation)
    {
        fromIncurred = FirstOfMonth(fromIncurred);
        toIncurred = FirstOfMonth(toIncurred);
        valuation = FirstOfMonth(valuation);
        if (toIncurred > valuation) toIncurred = valuation;

        var incremental = new Dictionary<DateOnly, decimal[]>();
        for (var m = fromIncurred; m <= toIncurred; m = m.AddMonths(1))
            incremental[m] = new decimal[MonthsBetween(m, valuation) + 1];

        foreach (var inc in increments)
        {
            if (!incremental.TryGetValue(inc.IncurredMonth, out var row) || inc.PaidMonth > valuation)
                continue;
            row[Math.Max(0, MonthsBetween(inc.IncurredMonth, inc.PaidMonth))] += inc.Amount;
        }

        var rows = incremental.OrderBy(kv => kv.Key).Select(kv =>
        {
            var cumulative = new decimal[kv.Value.Length];
            var running = 0m;
            for (var i = 0; i < kv.Value.Length; i++) { running += kv.Value[i]; cumulative[i] = running; }
            return new LagTriangleRow(kv.Key, cumulative);
        }).ToList();
        return new LagTriangle(valuation, rows, rows.Count == 0 ? 0 : rows.Max(r => r.LatestLag));
    }

    internal static DateOnly FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);
    internal static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);
    internal static int MonthsBetween(DateOnly from, DateOnly to) => (to.Year - from.Year) * 12 + to.Month - from.Month;
}
