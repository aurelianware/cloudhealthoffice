using System.Globalization;
using ClaimsService.Models;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// The stable identity of an edit on a claim. A reviewer's disposition
/// (override or uphold) is keyed by it, so when the claim is re-adjudicated
/// the pipeline recognises "the same edit the reviewer already decided on".
///
/// The key is built from the facts that make the edit what it is — rule,
/// codes, lines, date of service, matched prior claim — and never from the
/// message text, which can be reworded. If the claim facts change (a corrected
/// claim, a keying correction), the key changes and the edit needs a fresh
/// decision: an override is a decision about specific facts.
/// </summary>
public static class EditIdentity
{
    public static string Key(NcciEditFailureSnapshot edit)
    {
        var lines = Lines(edit.AffectedLineNumbers);
        return edit.EditType?.ToUpperInvariant() switch
        {
            "MUE" => Join("MUE", edit.RuleId, edit.Column2Code ?? edit.Column1Code,
                edit.ServiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), lines),
            "NCCIPAIR" or "NCCI_PAIR" => Join("NCCI", edit.RuleId, edit.Column1Code, edit.Column2Code, lines),
            _ => Join("EDIT", edit.EditType, edit.RuleId, edit.Column1Code, edit.Column2Code, lines),
        };
    }

    public static string Key(DuplicateFindingSnapshot finding) =>
        Join("DUP", finding.RuleId, finding.LineNumber.ToString(CultureInfo.InvariantCulture),
            finding.MatchedClaimId, finding.MatchedLineNumber?.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A plan edit is its rule and the lines it names. A key past the 200
    /// characters an edit key may hold (an edit naming very many lines) keeps
    /// its rule and replaces the line list with a hash of it, so it stays
    /// stable and unique.
    /// </summary>
    public static string Key(PlanEditFindingSnapshot edit)
    {
        var lines = Lines(edit.LineNumbers);
        var key = Join("PLAN", edit.RuleId, lines);
        if (key.Length <= MaxKeyLength) return key;

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lines));
        return Join("PLAN", edit.RuleId, "H" + Convert.ToHexString(hash, 0, 16));
    }

    /// <summary>The longest key an edit snapshot stores.</summary>
    public const int MaxKeyLength = 200;

    private static string Lines(IEnumerable<int>? lines) =>
        string.Join(",", (lines ?? []).Distinct().OrderBy(l => l));

    private static string Join(params string?[] parts) =>
        string.Join("|", parts.Select(p => (p ?? string.Empty).Trim().ToUpperInvariant()));
}
