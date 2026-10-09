using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Turns what each prior payer reported on an 837 (claim level: 2320 AMT*D
/// and CAS; line level: 2430 SVD02 and CAS) into per-line paid and patient
/// responsibility amounts for <see cref="CobLineInput.PriorPayers"/>.
///
/// <para><b>Line level wins; claim level is prorated by charge.</b> For each
/// prior payer:</para>
/// <list type="number">
///   <item><description>A line the payer reported in 2430 takes its SVD02
///     paid amount (summed if the payer reported the line more than once)
///     and the PR-group CAS of those 2430 loops as patient
///     responsibility.</description></item>
///   <item><description>The claim paid amount is 2320 AMT*D (the sum of the
///     2430 SVD02 amounts when AMT*D is absent). Whatever it differs from the
///     2430 total by is claim-level:
///     <list type="bullet">
///       <item><description>more than the 2430 total — prorated by charge
///         across the lines the payer did not report in 2430 (across all
///         lines when it reported every line);</description></item>
///       <item><description>less (005010 837 TR3: AMT*D = Σ 2430 SVD02 −
///         Σ 2320 CAS, i.e. claim-level adjustments taken after the line
///         payments) — taken off the 2430 line payments in proportion to
///         them, so no line goes below zero.</description></item>
///     </list></description></item>
///   <item><description>2320 claim-level PR CAS is prorated by charge across
///     the lines the payer did not report in 2430 (across all lines when it
///     reported every line).</description></item>
///   <item><description>Patient responsibility is known (non-null) for a
///     payer that reported any CAS at either level; with no CAS at all it is
///     null and the calculation does not use it.</description></item>
/// </list>
/// <para>Shares are truncated to the cent with the remainder on the last
/// line, so per payer the line amounts sum exactly to the claim amounts.
/// A DRG / per-stay unit uses the per-payer sums over all lines (= AMT*D).
/// X12 leaves the choice between SVD02 and AMT*D to the payer ("Payers should
/// indicate what prior payer payment amount impacted their payment for the
/// specific line" — X12 RFI #2806); preferring 2430 reports the prior
/// payment that actually applied to each line.</para>
/// </summary>
public static class PriorPayerAllocator
{
    /// <summary>A claim line as the allocator needs it: number and billed charge.</summary>
    public readonly record struct ClaimLineCharge(int LineNumber, decimal Charge);

    /// <summary>
    /// Per-line prior payer amounts for every line in <paramref name="lines"/>,
    /// each list ordered by payer sequence. Payers with a sequence at or
    /// after <paramref name="ourSequence"/> (not yet adjudicated) are ignored.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<PriorPayerAmount>> AllocateToLines(
        IReadOnlyList<ClaimLineCharge> lines,
        IEnumerable<PriorPayerAdjudication> priorPayers,
        int ourSequence)
    {
        // One entry per line number (a repeated number's charges combined).
        lines = lines
            .GroupBy(l => l.LineNumber)
            .Select(g => new ClaimLineCharge(g.Key, g.Sum(l => l.Charge)))
            .ToList();
        var result = lines.ToDictionary(l => l.LineNumber, _ => (IReadOnlyList<PriorPayerAmount>)new List<PriorPayerAmount>());
        foreach (var payer in priorPayers
                     .Where(p => p.Sequence > 0 && p.Sequence < ourSequence)
                     .OrderBy(p => p.Sequence))
        {
            foreach (var (lineNumber, amount) in AllocatePayer(lines, payer))
                ((List<PriorPayerAmount>)result[lineNumber]).Add(amount);
        }
        return result;
    }

    /// <summary>
    /// Claim-level (per-stay) prior payer amounts: each payer's line amounts
    /// summed, ordered by sequence.
    /// </summary>
    public static IReadOnlyList<PriorPayerAmount> AllocateToClaim(
        IReadOnlyList<ClaimLineCharge> lines,
        IEnumerable<PriorPayerAdjudication> priorPayers,
        int ourSequence)
    {
        var byLine = AllocateToLines(lines, priorPayers, ourSequence);
        return byLine.Values
            .SelectMany(v => v)
            .GroupBy(p => p.Sequence)
            .OrderBy(g => g.Key)
            .Select(g => new PriorPayerAmount
            {
                Sequence = g.Key,
                PaidAmount = g.Sum(p => p.PaidAmount),
                PatientResponsibility = g.Any(p => p.PatientResponsibility is null)
                    ? null
                    : g.Sum(p => p.PatientResponsibility!.Value),
            })
            .ToList();
    }

    private static IEnumerable<(int LineNumber, PriorPayerAmount Amount)> AllocatePayer(
        IReadOnlyList<ClaimLineCharge> lines, PriorPayerAdjudication payer)
    {
        var lineNumbers = lines.Select(l => l.LineNumber).ToHashSet();
        var reported = payer.Lines
            .Where(l => lineNumbers.Contains(l.LineNumber))
            .GroupBy(l => l.LineNumber)
            .ToDictionary(g => g.Key, g => (
                Paid: g.Sum(l => l.PaidAmount),
                Pr: g.SelectMany(l => l.Adjustments).Where(IsPatientResponsibility).Sum(a => a.Amount)));

        var prKnown = payer.ClaimAdjustments.Count > 0 || payer.Lines.Any(l => l.Adjustments.Count > 0);
        var claimPr = payer.ClaimAdjustments.Where(IsPatientResponsibility).Sum(a => a.Amount);

        var lineTotal = reported.Values.Sum(r => r.Paid);
        var claimPaid = payer.ClaimPaidAmount ?? lineTotal;
        var residual = claimPaid - lineTotal;

        // Lines that take claim-level amounts: those the payer did not report
        // in 2430, or every line when it reported them all.
        var unreported = lines.Where(l => !reported.ContainsKey(l.LineNumber)).ToList();
        var claimLevelTargets = unreported.Count > 0 ? unreported : lines.ToList();

        var paid = lines.ToDictionary(l => l.LineNumber, l => reported.TryGetValue(l.LineNumber, out var r) ? r.Paid : 0m);
        if (residual > 0)
        {
            var shares = Prorate(residual, claimLevelTargets.Select(l => l.Charge).ToList());
            for (var i = 0; i < claimLevelTargets.Count; i++)
                paid[claimLevelTargets[i].LineNumber] += shares[i];
        }
        else if (residual < 0 && lineTotal > 0)
        {
            var withPayment = lines.Where(l => paid[l.LineNumber] > 0).ToList();
            var cuts = Prorate(Math.Min(-residual, lineTotal), withPayment.Select(l => paid[l.LineNumber]).ToList());
            for (var i = 0; i < withPayment.Count; i++)
                paid[withPayment[i].LineNumber] -= Math.Min(cuts[i], paid[withPayment[i].LineNumber]);
        }

        var pr = lines.ToDictionary(l => l.LineNumber, l => reported.TryGetValue(l.LineNumber, out var r) ? r.Pr : 0m);
        if (claimPr > 0)
        {
            var shares = Prorate(claimPr, claimLevelTargets.Select(l => l.Charge).ToList());
            for (var i = 0; i < claimLevelTargets.Count; i++)
                pr[claimLevelTargets[i].LineNumber] += shares[i];
        }

        foreach (var line in lines)
        {
            yield return (line.LineNumber, new PriorPayerAmount
            {
                Sequence = payer.Sequence,
                PaidAmount = paid[line.LineNumber],
                PatientResponsibility = prKnown ? pr[line.LineNumber] : null,
            });
        }
    }

    private static bool IsPatientResponsibility(PriorPayerAdjustment a) =>
        string.Equals(a.GroupCode?.Trim(), "PR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <paramref name="amount"/> by <paramref name="weights"/>, each
    /// share truncated to the cent, the remainder on the last entry (equal
    /// shares when every weight is zero).
    /// </summary>
    internal static List<decimal> Prorate(decimal amount, IReadOnlyList<decimal> weights)
    {
        var shares = new List<decimal>(weights.Count);
        if (weights.Count == 0) return shares;

        var total = weights.Sum(w => Math.Max(0, w));
        decimal allocated = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (i == weights.Count - 1)
            {
                shares.Add(amount - allocated);
                break;
            }
            // Multiply before dividing so an exact share stays exact
            // (58 × 400 / 580 = 40.00, not 39.99).
            var share = total > 0
                ? Math.Truncate(amount * Math.Max(0, weights[i]) * 100m / total) / 100m
                : Math.Truncate(amount * 100m / weights.Count) / 100m;
            shares.Add(share);
            allocated += share;
        }
        return shares;
    }
}
