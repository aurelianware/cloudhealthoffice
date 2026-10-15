using ClaimsService.Models;
using ClaimsService.Services.Adjudication;

namespace ClaimsService.Services;

/// <summary>One edit on a claim's pend, as a reviewer decides it.</summary>
public sealed record ClaimEdit(
    string EditKey,
    string EditType,
    string RuleId,
    string Category,
    string? Message,
    IReadOnlyList<int> LineNumbers,
    string? SuggestedCarc,
    string? SuggestedRarc);

/// <summary>The decidable edits on a claim's pend: NCCI pair and MUE failures, and duplicate findings.</summary>
public static class ClaimEditCatalog
{
    public static IReadOnlyList<ClaimEdit> Edits(PendDetails? pend)
    {
        if (pend is null) return [];

        var edits = new List<ClaimEdit>();
        foreach (var e in pend.EditFailures ?? [])
        {
            if (e.EditType is not ("NcciPair" or "Mue")) continue; // an engine error isn't an edit to decide
            edits.Add(new(e.EditKey ?? EditIdentity.Key(e), e.EditType, e.RuleId,
                EditOverridePolicy.Category(e), e.Message, e.AffectedLineNumbers, e.SuggestedCarc, e.SuggestedRarc));
        }
        foreach (var d in pend.DuplicateFindings ?? [])
        {
            edits.Add(new(d.EditKey ?? EditIdentity.Key(d), "Duplicate", d.RuleId,
                EditOverridePolicy.Category(d), d.Message, [d.LineNumber], d.SuggestedCarc, null));
        }

        return edits
            .GroupBy(e => e.EditKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
