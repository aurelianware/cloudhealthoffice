using System.Globalization;
using System.Text;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// The discrepancies of a shadow run as CSV, for a plan's payment-integrity
/// team: one row per differing line, or one claim-level row (line number
/// empty) when the claim differs but no single line does. Matches and claims
/// that were not comparable are left out; claims CHO could not decide are
/// included only on request.
/// </summary>
public static class ShadowDiscrepancyCsv
{
    public static readonly string[] Header =
    [
        "ClaimNumber", "SourceSystem", "SourceClaimId", "ServiceDateFrom", "ClaimType", "BillingProviderNpi",
        "Kind", "CoreOutcome", "ChoOutcome", "LineNumber", "ProcedureCode", "RevenueCode", "Charge",
        "CoreAllowed", "ChoAllowed", "CorePaid", "ChoPaid", "PaidVariance",
        "CoreMemberLiability", "ChoMemberLiability", "CoreDenialCode", "ChoDenialCode",
        "Differences", "ChoPendCode", "ChoFindings",
    ];

    public static string Write(IEnumerable<ShadowAdjudicationResult> results, bool includeUndecided)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);

        foreach (var r in results)
        {
            if (r.Kind is ShadowDiscrepancyKind.Match or ShadowDiscrepancyKind.NotComparable) continue;
            if (r.Kind == ShadowDiscrepancyKind.ChoUndecided && !includeUndecided) continue;

            var differing = r.Lines.Where(l => l.Differences.Count > 0).ToList();
            if (differing.Count == 0 || r.Kind == ShadowDiscrepancyKind.ChoUndecided)
            {
                AppendRow(sb, ClaimRow(r));
                continue;
            }

            foreach (var line in differing)
                AppendRow(sb, LineRow(r, line));
        }

        return sb.ToString();
    }

    private static string?[] Common(ShadowAdjudicationResult r) =>
    [
        r.ClaimNumber, r.SourceSystem, r.SourceClaimId, r.ServiceDateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        r.ClaimType, r.BillingProviderNpi, r.Kind, r.CoreOutcome, r.ChoOutcome,
    ];

    private static string?[] ClaimRow(ShadowAdjudicationResult r) =>
    [
        .. Common(r),
        null, null, null, null,
        Money(r.CoreAllowed), Undecided(r) ? null : Money(r.ChoAllowed),
        Money(r.CorePaid), Undecided(r) ? null : Money(r.ChoPaid), Undecided(r) ? null : Money(r.PaidVariance),
        Money(r.CoreMemberLiability), Undecided(r) ? null : Money(r.ChoMemberLiability),
        r.CoreDenialCode, r.ChoDenialCode,
        null, r.ChoPendCode, string.Join(" | ", r.ChoStageFindings),
    ];

    private static string?[] LineRow(ShadowAdjudicationResult r, ShadowLineComparison l) =>
    [
        .. Common(r),
        l.LineNumber.ToString(CultureInfo.InvariantCulture), l.ProcedureCode, l.RevenueCode, Money(l.Charge),
        Money(l.CoreAllowed), Money(l.ChoAllowed), Money(l.CorePaid), Money(l.ChoPaid),
        l.CorePaid is { } core && l.ChoPaid is { } cho ? Money(cho - core) : null,
        Money(l.CoreMemberLiability), Money(l.ChoMemberLiability),
        l.CoreDenialCode, l.ChoDenialCode,
        string.Join(";", l.Differences), r.ChoPendCode, string.Join(" | ", r.ChoStageFindings),
    ];

    private static bool Undecided(ShadowAdjudicationResult r) => r.Kind == ShadowDiscrepancyKind.ChoUndecided;

    private static string? Money(decimal? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture);

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(fields[i]));
        }
        sb.Append("\r\n");
    }

    /// <summary>
    /// RFC 4180 quoting, plus spreadsheet formula neutralising: a text field
    /// starting with = + - @ (or a tab / carriage return) gets a leading
    /// apostrophe so Excel shows it instead of evaluating it. Amounts are
    /// formatted here, never from source text, so a negative amount is never
    /// touched.
    /// </summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '@' or '\t' or '\r'
            || (value[0] == '-' && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
