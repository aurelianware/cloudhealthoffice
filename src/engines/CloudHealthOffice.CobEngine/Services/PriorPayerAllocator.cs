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

    /// <summary>One payer's amounts for one line (see <see cref="AllocateDetailed"/>).</summary>
    /// <param name="Paid">2430 SVD02, plus its share of the 2320 residual.</param>
    /// <param name="ReportedPr">The PR-group CAS of the payer's 2430 loops for
    /// the line; null when the payer did not report the line in 2430.</param>
    public sealed record PayerLineAllocation(decimal Paid, decimal? ReportedPr)
    {
        public bool Reported => ReportedPr is not null;
    }

    /// <summary>
    /// One prior payer's adjudication as the claim-level calculation needs
    /// it: per-line paid amounts (2430 SVD02 for reported lines, the 2320
    /// claim residual prorated by charge across the others), line-level PR
    /// from 2430 exactly as reported, and the 2320 claim-level PR CAS kept
    /// as one amount (<see cref="ClaimPrPool"/>) — not prorated, because the
    /// claim-level calculation bounds by it at claim level.
    /// </summary>
    public sealed record PayerAllocation
    {
        public int Sequence { get; init; }
        public IReadOnlyDictionary<int, PayerLineAllocation> Lines { get; init; } = new Dictionary<int, PayerLineAllocation>();

        /// <summary>2320 PR-group CAS (claim level).</summary>
        public decimal ClaimPrPool { get; init; }

        /// <summary>The payer reported any CAS (claim or line level): its patient
        /// responsibility is known (0 when it reported no PR group).</summary>
        public bool PrKnown { get; init; }

        /// <summary>The lines the claim-level amounts belong to: those the payer
        /// did not report in 2430, or every line when it reported them all.</summary>
        public IReadOnlyList<int> ClaimLevelLines { get; init; } = [];

        /// <summary>
        /// The payer adjudicated the claim-level part: it paid something at
        /// claim level (2320 AMT*D above its 2430 total) or left the member
        /// a claim-level PR (2320 CAS*PR). A payer that paid $0 at claim level with only
        /// CO/OA adjustments (e.g. CO-27, CO-22, CO-109, CO-96, CO-204) did
        /// not cover the service.
        /// </summary>
        public bool ClaimLevelAdjudicated { get; init; }

        /// <summary>
        /// Whether this payer adjudicated (covered) <paramref name="lineNumber"/>:
        /// on the line itself it paid more than $0 or left a PR (2430), or the
        /// line is one its claim-level amounts belong to
        /// (<see cref="ClaimLevelLines"/>) and it paid or left a PR at claim
        /// level (<see cref="ClaimLevelAdjudicated"/>) — e.g. a $0 2430 line
        /// with the deductible reported only in 2320 CAS*PR. The same rule as
        /// the single-stay path, which sums a payer's line and claim amounts.
        /// A denial (paid $0, no PR anywhere — only CO/OA adjustments) is not an
        /// adjudication, so its PR of $0 must not bound what later payers pay.
        /// </summary>
        public bool AdjudicatedLine(int lineNumber) =>
            (Lines.TryGetValue(lineNumber, out var l) && l.Reported && (l.Paid > 0 || l.ReportedPr > 0))
            || (ClaimLevelAdjudicated && ClaimLevelLines.Contains(lineNumber));
    }

    /// <summary>
    /// Every prior payer (sequence before <paramref name="ourSequence"/>),
    /// ordered by sequence, allocated for the claim-level calculation.
    /// </summary>
    public static IReadOnlyList<PayerAllocation> AllocateDetailed(
        IReadOnlyList<ClaimLineCharge> lines,
        IEnumerable<PriorPayerAdjudication> priorPayers,
        int ourSequence)
    {
        lines = lines
            .GroupBy(l => l.LineNumber)
            .Select(g => new ClaimLineCharge(g.Key, g.Sum(l => l.Charge)))
            .ToList();
        return priorPayers
            .Where(p => p.Sequence > 0 && p.Sequence < ourSequence)
            .OrderBy(p => p.Sequence)
            .Select(p => AllocatePayerDetailed(lines, p))
            .ToList();
    }

    private static PayerAllocation AllocatePayerDetailed(
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

        return new PayerAllocation
        {
            Sequence = payer.Sequence,
            Lines = lines.ToDictionary(
                l => l.LineNumber,
                l => new PayerLineAllocation(
                    paid[l.LineNumber],
                    reported.TryGetValue(l.LineNumber, out var r) ? r.Pr : null)),
            ClaimPrPool = claimPr,
            PrKnown = prKnown,
            ClaimLevelLines = claimLevelTargets.Select(l => l.LineNumber).ToList(),
            // Unreported lines carry only the claim-level amounts; with every
            // line reported the claim level is a pure adjustment of the 2430
            // amounts, so "covered" follows the lines.
            ClaimLevelAdjudicated = residual > 0 || claimPr > 0,
        };
    }

    private static IEnumerable<(int LineNumber, PriorPayerAmount Amount)> AllocatePayer(
        IReadOnlyList<ClaimLineCharge> lines, PriorPayerAdjudication payer)
    {
        var detailed = AllocatePayerDetailed(lines, payer);

        // Per-line view: the claim-level PR prorated by charge across the
        // claim-level lines.
        var pr = lines.ToDictionary(l => l.LineNumber, l => detailed.Lines[l.LineNumber].ReportedPr ?? 0m);
        if (detailed.ClaimPrPool > 0)
        {
            var targets = detailed.ClaimLevelLines;
            var charges = lines.ToDictionary(l => l.LineNumber, l => l.Charge);
            var shares = Prorate(detailed.ClaimPrPool, targets.Select(n => charges[n]).ToList());
            for (var i = 0; i < targets.Count; i++)
                pr[targets[i]] += shares[i];
        }

        foreach (var line in lines)
        {
            yield return (line.LineNumber, new PriorPayerAmount
            {
                Sequence = payer.Sequence,
                PaidAmount = detailed.Lines[line.LineNumber].Paid,
                PatientResponsibility = detailed.PrKnown ? pr[line.LineNumber] : null,
            });
        }
    }

    private static bool IsPatientResponsibility(PriorPayerAdjustment a) =>
        string.Equals(a.GroupCode?.Trim(), "PR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <paramref name="amount"/> by <paramref name="weights"/>, each
    /// share truncated to the cent, the remainder on the last entry with a
    /// positive weight (equal shares, remainder on the last entry, when every
    /// weight is zero).
    /// </summary>
    internal static List<decimal> Prorate(decimal amount, IReadOnlyList<decimal> weights)
    {
        var shares = new List<decimal>(weights.Count);
        if (weights.Count == 0) return shares;

        var total = weights.Sum(w => Math.Max(0, w));
        // The remainder goes to the last entry with a positive weight, never
        // to a $0-charge line (equal shares → the last entry).
        var remainderIndex = weights.Count - 1;
        if (total > 0)
            while (weights[remainderIndex] <= 0) remainderIndex--;
        decimal allocated = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (i == remainderIndex) { shares.Add(0); continue; }
            // Multiply before dividing so an exact share stays exact
            // (58 × 400 / 580 = 40.00, not 39.99).
            var share = total > 0
                ? Math.Truncate(amount * Math.Max(0, weights[i]) * 100m / total) / 100m
                : Math.Truncate(amount * 100m / weights.Count) / 100m;
            shares.Add(share);
            allocated += share;
        }
        shares[remainderIndex] = amount - allocated;
        return shares;
    }
}
