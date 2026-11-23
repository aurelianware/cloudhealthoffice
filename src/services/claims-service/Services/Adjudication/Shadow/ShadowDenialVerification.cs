namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Every denial the core made, checked against CHO's answer: the claims the
/// core denied, and the lines it denied on claims it paid. Each denial gets
/// one verdict:
/// <list type="bullet">
///   <item><see cref="ShadowDenialVerdict.ChoWouldPay"/>: CHO, applying the
///   plan's own contracts, benefits and authorizations, would pay. The
///   wrongful-denial risk: an appeal, a provider complaint, in Medicaid a
///   finding the state may see.</item>
///   <item><see cref="ShadowDenialVerdict.ChoDeniesForAnotherReason"/>: CHO
///   denies too, with a different denial code. The denial may stand while
///   the reason on the remittance is wrong.</item>
///   <item><see cref="ShadowDenialVerdict.ChoConfirms"/>: CHO denies with the
///   same code.</item>
///   <item><see cref="ShadowDenialVerdict.ChoUndecided"/>: CHO could not
///   decide the claim yet (the report's "What to load next" says why).</item>
/// </list>
/// Grouped by the core's denial code (CARC), so a plan sees which denial
/// reasons hold up. Every verdict is a lead for the plan's review, not a
/// determination.
/// </summary>
public class ShadowDenialVerification
{
    /// <summary>Denials checked: claims the core denied plus lines it denied on claims it paid.</summary>
    public int CoreDenials { get; init; }

    /// <summary>Claims with at least one of those denials.</summary>
    public int Claims { get; init; }

    public ShadowDenialGroup ChoWouldPay { get; init; } = new();
    public ShadowDenialGroup ChoDeniesForAnotherReason { get; init; } = new();
    public ShadowDenialGroup ChoConfirms { get; init; } = new();
    public ShadowDenialGroup ChoUndecided { get; init; } = new();

    /// <summary>
    /// Of the denials CHO decided, the share it confirmed with the same code
    /// (0 to 1); null when CHO decided none.
    /// </summary>
    public decimal? ConfirmRate { get; init; }

    /// <summary>One row per core denial code, the most CHO-would-pay dollars first, then the most denials.</summary>
    public List<ShadowDenialCodeRow> ByCoreDenialCode { get; init; } = [];
}

/// <summary>The four verdicts, as stored in report keys and the portal.</summary>
public static class ShadowDenialVerdict
{
    public const string ChoWouldPay = "ChoWouldPay";
    public const string ChoDeniesForAnotherReason = "ChoDeniesForAnotherReason";
    public const string ChoConfirms = "ChoConfirms";
    public const string ChoUndecided = "ChoUndecided";
}

/// <summary>The denials that got one verdict.</summary>
public class ShadowDenialGroup
{
    public int Denials { get; set; }
    public int Claims { get; set; }

    /// <summary>Billed charges on the denied claims or lines.</summary>
    public decimal Billed { get; set; }

    /// <summary>What CHO would have paid on them: nonzero only where CHO would pay.</summary>
    public decimal ChoWouldPayAmount { get; set; }

    /// <summary>Up to five claim numbers to look at first, largest CHO-would-pay amount first.</summary>
    public List<string> ExampleClaims { get; set; } = [];
}

/// <summary>One core denial code and what CHO said about its denials.</summary>
public class ShadowDenialCodeRow
{
    /// <summary>The core's CARC, or "none" when the core's file gave no code.</summary>
    public string CoreDenialCode { get; set; } = string.Empty;

    public int Denials { get; set; }
    public int ChoWouldPay { get; set; }
    public int ChoDeniesForAnotherReason { get; set; }
    public int ChoConfirms { get; set; }
    public int ChoUndecided { get; set; }

    /// <summary>What CHO would have paid on this code's denials it would pay.</summary>
    public decimal ChoWouldPayAmount { get; set; }

    public decimal Billed { get; set; }

    /// <summary>Confirmed of decided (0 to 1); null when CHO decided none of this code's denials.</summary>
    public decimal? ConfirmRate { get; set; }

    /// <summary>
    /// The codes CHO denied with instead, most frequent first, at most three:
    /// "16 (12)" means CHO used 16 on twelve of these denials.
    /// </summary>
    public List<string> ChoCodesInstead { get; set; } = [];

    /// <summary>Up to five claim numbers, the ones CHO would pay first.</summary>
    public List<string> ExampleClaims { get; set; } = [];
}

public static class ShadowDenialVerificationBuilder
{
    private const int Examples = 5;
    private const string NoCode = "none";

    /// <summary>One core denial and CHO's verdict on it.</summary>
    internal sealed record Denial(
        ShadowAdjudicationResult Claim, int? LineNumber, string CoreCode, string? ChoCode,
        string Verdict, decimal Billed, decimal ChoWouldPay);

    public static ShadowDenialVerification Build(IReadOnlyList<ShadowAdjudicationResult> results, int topN)
    {
        ArgumentNullException.ThrowIfNull(results);
        var denials = results.SelectMany(Denials).ToList();
        var decided = denials.Count(d => d.Verdict != ShadowDenialVerdict.ChoUndecided);
        var confirmed = denials.Count(d => d.Verdict == ShadowDenialVerdict.ChoConfirms);

        return new ShadowDenialVerification
        {
            CoreDenials = denials.Count,
            Claims = denials.Select(d => d.Claim.Id).Distinct().Count(),
            ChoWouldPay = Group(denials, ShadowDenialVerdict.ChoWouldPay),
            ChoDeniesForAnotherReason = Group(denials, ShadowDenialVerdict.ChoDeniesForAnotherReason),
            ChoConfirms = Group(denials, ShadowDenialVerdict.ChoConfirms),
            ChoUndecided = Group(denials, ShadowDenialVerdict.ChoUndecided),
            ConfirmRate = Rate(confirmed, decided),
            ByCoreDenialCode = denials
                .GroupBy(d => d.CoreCode, StringComparer.OrdinalIgnoreCase)
                .Select(CodeRow)
                .OrderByDescending(r => r.ChoWouldPayAmount)
                .ThenByDescending(r => r.Denials)
                .ThenBy(r => r.CoreDenialCode, StringComparer.Ordinal)
                .Take(Math.Clamp(topN, 1, 100))
                .ToList(),
        };
    }

    /// <summary>
    /// The core's denials on one claim: the claim itself when the core denied
    /// it, otherwise each line the core denied on a claim it paid. A claim
    /// whose core answer is not final has none.
    /// </summary>
    internal static IEnumerable<Denial> Denials(ShadowAdjudicationResult r)
    {
        if (r.Kind == ShadowDiscrepancyKind.NotComparable) yield break;
        var undecided = r.Kind == ShadowDiscrepancyKind.ChoUndecided;

        if (r.CoreOutcome == ShadowOutcome.Denied)
        {
            var verdict = undecided ? ShadowDenialVerdict.ChoUndecided
                : r.ChoOutcome == ShadowOutcome.Paid ? ShadowDenialVerdict.ChoWouldPay
                : r.ChoOutcome == ShadowOutcome.Denied
                    ? SameCode(r.CoreDenialCode, r.ChoDenialCode)
                        ? ShadowDenialVerdict.ChoConfirms
                        : ShadowDenialVerdict.ChoDeniesForAnotherReason
                : ShadowDenialVerdict.ChoUndecided;
            yield return new Denial(
                r, null, Code(r.CoreDenialCode), Blank(r.ChoDenialCode), verdict,
                r.Lines.Sum(l => l.Charge),
                verdict == ShadowDenialVerdict.ChoWouldPay ? r.ChoPaid : 0m);
            yield break;
        }

        if (r.CoreOutcome != ShadowOutcome.Paid) yield break;

        foreach (var line in r.Lines.Where(l => l.CoreDenialCode is not null))
        {
            string verdict;
            if (undecided) verdict = ShadowDenialVerdict.ChoUndecided;
            else if (line.ChoDenialCode is null) verdict = ShadowDenialVerdict.ChoWouldPay;
            else verdict = SameCode(line.CoreDenialCode, line.ChoDenialCode)
                ? ShadowDenialVerdict.ChoConfirms
                : ShadowDenialVerdict.ChoDeniesForAnotherReason;
            yield return new Denial(
                r, line.LineNumber, Code(line.CoreDenialCode), Blank(line.ChoDenialCode), verdict,
                line.Charge,
                verdict == ShadowDenialVerdict.ChoWouldPay ? line.ChoPaid ?? 0m : 0m);
        }
    }

    private static ShadowDenialGroup Group(IEnumerable<Denial> denials, string verdict)
    {
        var list = denials.Where(d => d.Verdict == verdict).ToList();
        return new ShadowDenialGroup
        {
            Denials = list.Count,
            Claims = list.Select(d => d.Claim.Id).Distinct().Count(),
            Billed = list.Sum(d => d.Billed),
            ChoWouldPayAmount = list.Sum(d => d.ChoWouldPay),
            ExampleClaims = TopExamples(list),
        };
    }

    private static ShadowDenialCodeRow CodeRow(IGrouping<string, Denial> g)
    {
        var list = g.ToList();
        int Count(string verdict) => list.Count(d => d.Verdict == verdict);
        var confirms = Count(ShadowDenialVerdict.ChoConfirms);
        var undecided = Count(ShadowDenialVerdict.ChoUndecided);
        return new ShadowDenialCodeRow
        {
            CoreDenialCode = g.Key,
            Denials = list.Count,
            ChoWouldPay = Count(ShadowDenialVerdict.ChoWouldPay),
            ChoDeniesForAnotherReason = Count(ShadowDenialVerdict.ChoDeniesForAnotherReason),
            ChoConfirms = confirms,
            ChoUndecided = undecided,
            ChoWouldPayAmount = list.Sum(d => d.ChoWouldPay),
            Billed = list.Sum(d => d.Billed),
            ConfirmRate = Rate(confirms, list.Count - undecided),
            ChoCodesInstead = list
                .Where(d => d.Verdict == ShadowDenialVerdict.ChoDeniesForAnotherReason)
                .GroupBy(d => d.ChoCode ?? NoCode, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(c => c.Count()).ThenBy(c => c.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(c => $"{c.Key} ({c.Count()})")
                .ToList(),
            ExampleClaims = TopExamples(list),
        };
    }

    private static List<string> TopExamples(IEnumerable<Denial> denials) =>
        denials
            .OrderBy(d => d.Verdict == ShadowDenialVerdict.ChoWouldPay ? 0 : 1)
            .ThenByDescending(d => d.ChoWouldPay)
            .ThenByDescending(d => d.Billed)
            .Select(d => d.Claim.ClaimNumber)
            .Distinct(StringComparer.Ordinal)
            .Take(Examples)
            .ToList();

    private static decimal? Rate(int part, int whole) =>
        whole <= 0 ? null : Math.Round((decimal)part / whole, 4);

    private static string Code(string? code) => Blank(code) ?? NoCode;

    private static bool SameCode(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
