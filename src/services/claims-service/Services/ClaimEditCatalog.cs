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

/// <summary>The decidable edits on a claim's pend: NCCI pair and MUE failures, duplicate findings, and the plan's own edits.</summary>
public static class ClaimEditCatalog
{
    /// <summary><see cref="ClaimEdit.EditType"/> of an edit from the plan's edit service (ADR 019).</summary>
    public const string PlanEditType = "PlanEdit";

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

        foreach (var p in pend.PlanEditFindings ?? [])
        {
            edits.Add(new(p.EditKey ?? EditIdentity.Key(p), PlanEditType, p.RuleId,
                EditOverridePolicy.Category(p), p.Message, p.LineNumbers, p.SuggestedCarc, p.SuggestedRarc));
        }

        return edits
            .GroupBy(e => e.EditKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
