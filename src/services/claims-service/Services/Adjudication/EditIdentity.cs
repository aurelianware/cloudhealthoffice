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

    private static string Lines(IEnumerable<int>? lines) =>
        string.Join(",", (lines ?? []).Distinct().OrderBy(l => l));

    private static string Join(params string?[] parts) =>
        string.Join("|", parts.Select(p => (p ?? string.Empty).Trim().ToUpperInvariant()));
}
