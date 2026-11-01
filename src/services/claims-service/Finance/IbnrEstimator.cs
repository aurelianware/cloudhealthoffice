namespace ClaimsService.Finance;

/// <summary>How IBNR is estimated from a lag triangle.</summary>
public sealed record IbnrOptions
{
    /// <summary>
    /// Development factors use only the most recent this-many incurred months
    /// that observe both lags (volume-weighted), so old payment patterns fade.
    /// </summary>
    public int FactorMonths { get; init; } = 12;

    /// <summary>
    /// Lags beyond this are treated as complete (factor 1). Medicaid claims
    /// are mostly complete within a year; filing limits make later money rare.
    /// </summary>
    public int MaxDevelopmentLag { get; init; } = 24;

    /// <summary>
    /// Below this completion factor the paid-to-date is too thin to develop
    /// (typically the last two or three months): when an expected PMPM and
    /// member-months are given for the month, ultimate is PMPM × member-months
    /// instead. 0 turns the fallback off.
    /// </summary>
    public decimal PmpmFallbackBelowCompletion { get; init; } = 0.60m;

    /// <summary>Member-months per incurred month (for PMPM figures and the fallback).</summary>
    public IReadOnlyDictionary<DateOnly, int>? MemberMonths { get; init; }

    /// <summary>Expected incurred PMPM for the fallback (e.g. from the rate-setting or the trailing 12 months).</summary>
    public decimal? ExpectedPmpm { get; init; }
}

public enum UltimateMethod
{
    /// <summary>Paid to date divided by the completion factor.</summary>
    ChainLadder = 1,

    /// <summary>Expected PMPM × member-months, because the month is too immature to develop.</summary>
    ProjectedPmpm = 2,
}

/// <summary>One incurred month's estimate.</summary>
public sealed record IbnrMonth(
    DateOnly IncurredMonth,
    int Lag,
    decimal PaidToDate,
    decimal CompletionFactor,
    decimal Ultimate,
    decimal Ibnr,
    UltimateMethod Method,
    int? MemberMonths)
{
    public decimal? UltimatePmpm => MemberMonths is > 0 ? Math.Round(Ultimate / MemberMonths.Value, 2) : null;
}

/// <summary>The whole estimate: per month, totals, and the factors behind it (for the actuary's review).</summary>
public sealed record IbnrEstimate(
    DateOnly ValuationMonth,
    IReadOnlyList<IbnrMonth> Months,
    IReadOnlyList<decimal> DevelopmentFactors,
    IReadOnlyList<decimal> CompletionFactors,
    IReadOnlyList<string> Notes)
{
    public decimal TotalPaid => Months.Sum(m => m.PaidToDate);
    public decimal TotalUltimate => Months.Sum(m => m.Ultimate);
    public decimal TotalIbnr => Months.Sum(m => m.Ibnr);
}

/// <summary>
/// Chain-ladder IBNR with a projected-PMPM fallback for immature months: the
/// standard health reserving method plans book monthly and their actuary
/// certifies yearly. This is a starting estimate for the close, with the
/// factors shown, not an actuarial opinion; margins (adverse deviation, claim
/// adjustment expense) are the actuary's to add.
/// </summary>
public static class IbnrEstimator
{
    public static IbnrEstimate Estimate(LagTriangle triangle, IbnrOptions? options = null)
    {
        options ??= new IbnrOptions();
        var notes = new List<string>();
        var maxLag = Math.Min(triangle.MaxLag, options.MaxDevelopmentLag);

        // Development factor lag k -> k+1: Σ C(i,k+1) / Σ C(i,k) over the most
        // recent rows that observe lag k+1.
        var factors = new decimal[Math.Max(maxLag, 0)];
        for (var k = 0; k < maxLag; k++)
        {
            var observed = triangle.Rows.Where(r => r.LatestLag >= k + 1)
                .OrderByDescending(r => r.IncurredMonth).Take(options.FactorMonths).ToList();
            var from = observed.Sum(r => r.CumulativePaid[k]);
            var to = observed.Sum(r => r.CumulativePaid[k + 1]);
            if (observed.Count == 0 || from <= 0m)
            {
                factors[k] = 1m;
                notes.Add($"Lag {k}->{k + 1}: no paid history to develop from; factor 1.0 used");
                continue;
            }
            factors[k] = to / from;
        }

        // Completion factor at lag k: share of ultimate paid by lag k.
        var completion = new decimal[maxLag + 1];
        var cumulative = 1m;
        for (var k = maxLag; k >= 0; k--)
        {
            completion[k] = cumulative == 0m ? 1m : 1m / cumulative;
            if (k > 0) cumulative *= factors[k - 1];
        }

        var months = new List<IbnrMonth>();
        foreach (var row in triangle.Rows)
        {
            var lag = Math.Min(row.LatestLag, maxLag);
            var cf = completion[lag];
            var paid = row.PaidToDate;
            int? mm = options.MemberMonths != null && options.MemberMonths.TryGetValue(row.IncurredMonth, out var n) ? n : null;

            var method = UltimateMethod.ChainLadder;
            decimal ultimate;
            if (cf < options.PmpmFallbackBelowCompletion && options.ExpectedPmpm is { } pmpm && mm is > 0)
            {
                ultimate = Math.Max(paid, pmpm * mm.Value);
                method = UltimateMethod.ProjectedPmpm;
            }
            else
            {
                ultimate = cf > 0m ? paid / cf : paid;
                if (cf < options.PmpmFallbackBelowCompletion)
                    notes.Add($"{row.IncurredMonth:yyyy-MM}: only {cf:P0} complete and no PMPM fallback given; chain-ladder estimate is volatile");
            }

            ultimate = Math.Round(ultimate, 2, MidpointRounding.AwayFromZero);
            months.Add(new IbnrMonth(row.IncurredMonth, row.LatestLag, paid, Math.Round(cf, 4), ultimate,
                ultimate - paid, method, mm));
        }

        return new IbnrEstimate(triangle.ValuationMonth, months,
            factors.Select(f => Math.Round(f, 4)).ToList(), completion.Select(c => Math.Round(c, 4)).ToList(), notes);
    }
}
