using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Money;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// A line (or some of its units) denied because a reviewer upheld an edit.
/// Group CO: an NCCI, MUE or duplicate denial is the provider's liability.
/// </summary>
public sealed record UpheldEditLineDenial(
    int LineNumber,
    decimal DeniedUnits,
    decimal LineUnits,
    string Carc,
    string? Rarc,
    string EditKey,
    string Group = "CO")
{
    public bool IsFullLine => DeniedUnits >= LineUnits;

    /// <summary>The share of the line that stays payable (0 for a full-line denial).</summary>
    public decimal PayableFraction => LineUnits <= 0 ? 0m : Math.Max(0m, (LineUnits - DeniedUnits) / LineUnits);
}

/// <summary>
/// Turns a reviewer's edit dispositions into pipeline effects on the
/// approval re-run (<see cref="ExaminerApproval.EditDispositions"/>):
/// <list type="bullet">
///   <item><description><b>Override</b> — the edit stage suppresses the edit (it neither pends nor denies).</description></item>
///   <item><description><b>Uphold</b> — the edit stage treats it as decided, and benefit calculation denies
///     what the edit covers: the column-2 line of an NCCI pair, the units over an MUE limit, the duplicate line.</description></item>
/// </list>
/// An edit that fires with no disposition is one the reviewer never saw; it
/// pends as usual and the approval is refused.
/// </summary>
public static class EditDispositionApplier
{
    public const string NcciCarcFallback = "97";
    public const string MueCarc = "151";
    public const string DuplicateCarc = "18";

    public static EditDisposition? DispositionFor(ClaimAdjudicationContext context, string? editKey)
    {
        if (string.IsNullOrWhiteSpace(editKey)) return null;
        return context.ExaminerApproval?.EditDispositions
            .FirstOrDefault(d => string.Equals(d.EditKey, editKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The line denials the reviewer's upheld edits call for, computed from
    /// the pend the reviewer saw (<see cref="ExaminerApproval.ReviewedPend"/>).
    /// </summary>
    public static IReadOnlyList<UpheldEditLineDenial> LineDenials(ClaimAdjudicationContext context)
    {
        var approval = context.ExaminerApproval;
        if (approval is null || (approval.EditDispositions.Count == 0 && approval.ManualLineDenials.Count == 0)) return [];

        var upheld = approval.EditDispositions
            .Where(d => d.Decision == EditDecision.Uphold)
            .Select(d => d.EditKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = context.Claim.ClaimLines.ToDictionary(l => l.LineNumber);
        var denials = new List<UpheldEditLineDenial>();

        foreach (var manual in approval.ManualLineDenials)
        {
            if (!lines.TryGetValue(manual.LineNumber, out var line)) continue;
            var units = Math.Min(line.Units, manual.Units ?? line.Units);
            denials.Add(new(manual.LineNumber, units, line.Units, manual.Carc, manual.Rarc,
                $"MANUAL|{manual.GroupCode}-{manual.Carc}", manual.GroupCode));
        }
        var pend = approval.ReviewedPend;

        foreach (var edit in pend?.EditFailures ?? [])
        {
            var key = edit.EditKey ?? EditIdentity.Key(edit);
            if (!upheld.Contains(key)) continue;

            if (string.Equals(edit.EditType, "Mue", StringComparison.OrdinalIgnoreCase))
            {
                AddMueDenials(denials, edit, key, lines);
            }
            else
            {
                foreach (var lineNumber in edit.AffectedLineNumbers.Distinct())
                {
                    if (!lines.TryGetValue(lineNumber, out var line)) continue;
                    denials.Add(new(lineNumber, line.Units, line.Units,
                        edit.SuggestedCarc ?? NcciCarcFallback, edit.SuggestedRarc, key));
                }
            }
        }

        foreach (var finding in pend?.DuplicateFindings ?? [])
        {
            var key = finding.EditKey ?? EditIdentity.Key(finding);
            if (!upheld.Contains(key) || !lines.TryGetValue(finding.LineNumber, out var line)) continue;
            denials.Add(new(finding.LineNumber, line.Units, line.Units,
                finding.SuggestedCarc ?? DuplicateCarc, null, key));
        }

        // One denial per line: a line denied in full by one edit and partly by
        // another is denied in full; two partial denials add up, capped at the line.
        return denials
            .GroupBy(d => d.LineNumber)
            .Select(g =>
            {
                var full = g.FirstOrDefault(d => d.IsFullLine);
                if (full is not null) return full;
                var first = g.First();
                return first with { DeniedUnits = Math.Min(first.LineUnits, g.Sum(d => d.DeniedUnits)) };
            })
            .OrderBy(d => d.LineNumber)
            .ToList();
    }

    /// <summary>
    /// The units over the MUE limit are denied, taken from the last affected
    /// line backwards, so the earliest units billed are the ones paid.
    /// </summary>
    private static void AddMueDenials(
        List<UpheldEditLineDenial> denials,
        NcciEditFailureSnapshot edit,
        string key,
        IReadOnlyDictionary<int, AdapterClaimLine> lines)
    {
        var affected = edit.AffectedLineNumbers
            .Distinct()
            .Where(lines.ContainsKey)
            .Select(n => lines[n])
            .OrderBy(l => l.LineNumber)
            .ToList();
        if (affected.Count == 0 || edit.MueMaxUnits is not { } max) return;

        var billed = edit.UnitsBilled ?? affected.Sum(l => l.Units);
        var excess = billed - max;
        for (var i = affected.Count - 1; i >= 0 && excess > 0; i--)
        {
            var line = affected[i];
            var take = Math.Min(line.Units, excess);
            excess -= take;
            denials.Add(new(line.LineNumber, take, line.Units, edit.SuggestedCarc ?? MueCarc, edit.SuggestedRarc, key));
        }
    }

    /// <summary>Reduces each denied line's priced allowed amount to its payable share.</summary>
    public static void ApplyToAllowed(IDictionary<int, decimal> allowedAmounts, IReadOnlyList<UpheldEditLineDenial> denials)
    {
        foreach (var denial in denials)
        {
            if (allowedAmounts.TryGetValue(denial.LineNumber, out var allowed))
            {
                allowedAmounts[denial.LineNumber] = MoneyRounding.ToCents(allowed * denial.PayableFraction);
            }
        }
    }

    /// <summary>
    /// Writes each denial onto its line's adjustments as CO-{CARC} for the
    /// denied share of the charge, taken out of the line's CO-45 so the line
    /// still balances (charge = allowed + adjustments).
    /// </summary>
    public static void ApplyToLineAdjustments(
        IReadOnlyList<AdapterClaimLine> claimLines,
        IList<LineAdjudicationResult> lineResults,
        IReadOnlyList<UpheldEditLineDenial> denials)
    {
        for (var i = 0; i < claimLines.Count && i < lineResults.Count; i++)
        {
            var line = claimLines[i];
            var denial = denials.FirstOrDefault(d => d.LineNumber == line.LineNumber);
            if (denial is null) continue;

            var deniedCharge = MoneyRounding.ToCents(line.ChargeAmount * (1m - denial.PayableFraction));
            if (deniedCharge <= 0) continue;

            var reasons = lineResults[i].AdjustmentReasons;
            var co45 = reasons.FirstOrDefault(r => r.GroupCode == "CO" && r.ReasonCode == "45");
            if (co45 is not null)
            {
                co45.Amount = Math.Max(0m, co45.Amount - deniedCharge);
                if (co45.Amount == 0m) reasons.Remove(co45);
            }

            reasons.Insert(0, new ClaimAdjustmentReason
            {
                GroupCode = denial.Group,
                ReasonCode = denial.Carc,
                RemarkCode = denial.Rarc,
                Amount = deniedCharge,
            });
        }
    }
}
