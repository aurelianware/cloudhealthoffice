namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// What a plan reads after a shadow run: how much of its volume CHO decided,
/// how often CHO agreed with the core, where the money differs, and why.
/// Built from the stored results by <see cref="ShadowRunReportBuilder"/>.
/// </summary>
public class ShadowRunReport
{
    public required ShadowAdjudicationRun Run { get; init; }

    /// <summary>Claims whose core answer is final (everything but NotComparable).</summary>
    public int ComparableClaims { get; init; }

    /// <summary>Comparable claims CHO decided (paid or denied).</summary>
    public int DecidedClaims { get; init; }

    /// <summary>Decided claims where the core paid more than CHO would have: possible overpayments.</summary>
    public ShadowMoneyBucket CorePaidMore { get; init; } = new();

    /// <summary>Decided claims where the core paid less than CHO would have: possible underpayments.</summary>
    public ShadowMoneyBucket CorePaidLess { get; init; } = new();

    /// <summary>One row per kind, in <see cref="ShadowDiscrepancyKind.All"/> order.</summary>
    public List<ShadowReasonRow> ByKind { get; init; } = [];

    /// <summary>Why CHO could not decide: one row per pend / gap code, most claims first.</summary>
    public List<ShadowReasonRow> UndecidedByGap { get; init; } = [];

    /// <summary>
    /// Decided claims CHO would still hold because it could not screen a provider
    /// (PROVIDERSCREEN): counted among the decided, and listed here so the run says
    /// that screening is still to be set up.
    /// </summary>
    public ShadowReasonRow HeldForProviderScreening { get; init; } = new();

    /// <summary>The CHO stage whose finding drove a non-matching decision, largest dollars first.</summary>
    public List<ShadowReasonRow> ByChoStage { get; init; } = [];

    /// <summary>Denial disagreements as "core CARC → CHO CARC" ("paid" when that side paid).</summary>
    public List<ShadowReasonRow> ByDenialCodes { get; init; } = [];

    /// <summary>Procedure (or revenue) codes on lines that differ, largest line dollars first.</summary>
    public List<ShadowReasonRow> ByProcedure { get; init; } = [];

    /// <summary>Billing providers on non-matching decided claims, largest dollars first.</summary>
    public List<ShadowReasonRow> ByBillingProvider { get; init; } = [];

    public int TopN { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public class ShadowMoneyBucket
{
    public int Claims { get; set; }

    /// <summary>Sum of |CHO paid − core paid| over these claims.</summary>
    public decimal Amount { get; set; }
}

/// <summary>One reason in a report breakdown.</summary>
public class ShadowReasonRow
{
    public string Key { get; set; } = string.Empty;
    public int Claims { get; set; }

    /// <summary>Lines, for line-level breakdowns (procedure); otherwise 0.</summary>
    public int Lines { get; set; }

    /// <summary>Sum of CHO paid − core paid. Positive: CHO would pay more.</summary>
    public decimal NetPaidVariance { get; set; }

    /// <summary>Sum of |CHO paid − core paid|.</summary>
    public decimal AbsolutePaidVariance { get; set; }

    /// <summary>Core paid on these claims (for an undecided gap: the money CHO cannot yet check).</summary>
    public decimal CorePaid { get; set; }

    /// <summary>Up to five claim numbers to look at first.</summary>
    public List<string> ExampleClaims { get; set; } = [];
}

public static class ShadowRunReportBuilder
{
    public const int DefaultTopN = 10;
    private const int Examples = 5;

    public static ShadowRunReport Build(
        ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, int topN = DefaultTopN)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);
        topN = Math.Clamp(topN, 1, 100);

        var comparable = results.Where(r => r.Kind != ShadowDiscrepancyKind.NotComparable).ToList();
        var undecided = comparable.Where(r => r.Kind == ShadowDiscrepancyKind.ChoUndecided).ToList();
        var decided = comparable.Where(r => r.Kind != ShadowDiscrepancyKind.ChoUndecided).ToList();
        var differing = decided.Where(r => r.Kind != ShadowDiscrepancyKind.Match).ToList();

        return new ShadowRunReport
        {
            Run = run,
            TopN = topN,
            ComparableClaims = comparable.Count,
            DecidedClaims = decided.Count,
            CorePaidMore = Bucket(decided.Where(r => r.PaidVariance < 0)),
            CorePaidLess = Bucket(decided.Where(r => r.PaidVariance > 0)),
            ByKind = ShadowDiscrepancyKind.All
                .Select(kind => Row(kind, results.Where(r => r.Kind == kind)))
                .ToList(),
            HeldForProviderScreening = Row(
                ShadowDiscrepancyComparer.ProviderScreeningCode, decided.Where(r => r.HeldForProviderScreening)),
            UndecidedByGap = Top(
                undecided.GroupBy(r => r.ChoPendCode ?? "UNKNOWN", StringComparer.OrdinalIgnoreCase)
                    .Select(g => Row(g.Key, g)),
                topN, byMoney: false),
            ByChoStage = Top(
                differing.GroupBy(DrivingStage, StringComparer.Ordinal).Select(g => Row(g.Key, g)),
                topN, byMoney: true),
            ByDenialCodes = Top(
                differing
                    .Where(r => r.Kind is ShadowDiscrepancyKind.OutcomeMismatch or ShadowDiscrepancyKind.DenialCodeMismatch)
                    .GroupBy(r => $"{r.CoreDenialCode ?? "paid"} → {r.ChoDenialCode ?? "paid"}", StringComparer.Ordinal)
                    .Select(g => Row(g.Key, g)),
                topN, byMoney: true),
            ByProcedure = Top(ProcedureRows(differing), topN, byMoney: true),
            ByBillingProvider = Top(
                differing.GroupBy(r => r.BillingProviderNpi ?? "unknown", StringComparer.Ordinal)
                    .Select(g => Row(g.Key, g)),
                topN, byMoney: true),
        };
    }

    /// <summary>
    /// The stage behind a non-matching decision: the first CHO stage finding
    /// ("Stage: reason"), or "Pricing" for an allowed difference and
    /// "BenefitCalculation" for a payment or member-share difference when no
    /// stage reported one (the claim passed every stage, the amounts differ).
    /// </summary>
    internal static string DrivingStage(ShadowAdjudicationResult r)
    {
        if (r.ChoStageFindings.FirstOrDefault() is { Length: > 0 } finding)
        {
            var colon = finding.IndexOf(':');
            return colon > 0 ? finding[..colon] : finding;
        }
        return r.Kind switch
        {
            ShadowDiscrepancyKind.AllowedVariance => Stages.PricingStage.StageName,
            ShadowDiscrepancyKind.PaidVariance or ShadowDiscrepancyKind.MemberLiabilityVariance
                => Stages.BenefitCalculationStage.StageName,
            _ => "Unattributed",
        };
    }

    private static IEnumerable<ShadowReasonRow> ProcedureRows(IEnumerable<ShadowAdjudicationResult> differing)
    {
        var lines = differing
            .SelectMany(r => r.Lines.Where(l => l.Differences.Count > 0).Select(l => (Claim: r, Line: l)));
        return lines
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Line.ProcedureCode)
                ? $"REV {x.Line.RevenueCode ?? "?"}"
                : x.Line.ProcedureCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var row = new ShadowReasonRow
                {
                    Key = g.Key,
                    Claims = g.Select(x => x.Claim.Id).Distinct().Count(),
                    Lines = g.Count(),
                    ExampleClaims = g.Select(x => x.Claim.ClaimNumber).Distinct().Take(Examples).ToList(),
                };
                foreach (var (_, line) in g)
                {
                    var diff = (line.ChoPaid ?? 0m) - (line.CorePaid ?? 0m);
                    if (line.ChoPaid is null || line.CorePaid is null) diff = 0m;
                    row.NetPaidVariance += diff;
                    row.AbsolutePaidVariance += Math.Abs(diff);
                    row.CorePaid += line.CorePaid ?? 0m;
                }
                return row;
            });
    }

    private static ShadowReasonRow Row(string key, IEnumerable<ShadowAdjudicationResult> group)
    {
        var list = group as IList<ShadowAdjudicationResult> ?? group.ToList();
        return new ShadowReasonRow
        {
            Key = key,
            Claims = list.Count,
            NetPaidVariance = list.Sum(r => r.PaidVariance),
            AbsolutePaidVariance = list.Sum(r => r.AbsolutePaidVariance),
            CorePaid = list.Sum(r => r.CorePaid),
            ExampleClaims = list.OrderByDescending(r => r.AbsolutePaidVariance)
                .Select(r => r.ClaimNumber).Distinct().Take(Examples).ToList(),
        };
    }

    private static ShadowMoneyBucket Bucket(IEnumerable<ShadowAdjudicationResult> results)
    {
        var list = results.ToList();
        return new ShadowMoneyBucket { Claims = list.Count, Amount = list.Sum(r => r.AbsolutePaidVariance) };
    }

    private static List<ShadowReasonRow> Top(IEnumerable<ShadowReasonRow> rows, int topN, bool byMoney) =>
        (byMoney
            ? rows.OrderByDescending(r => r.AbsolutePaidVariance).ThenByDescending(r => r.Claims)
            : rows.OrderByDescending(r => r.Claims).ThenByDescending(r => r.CorePaid))
        .ThenBy(r => r.Key, StringComparer.Ordinal)
        .Take(topN)
        .ToList();
}
