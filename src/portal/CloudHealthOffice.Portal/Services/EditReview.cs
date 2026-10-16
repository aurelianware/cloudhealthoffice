using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>claims-service <c>GET /claims/{id}/edits</c>: the edits on a pended claim as a reviewer decides them.</summary>
public sealed class ClaimEditsView
{
    public string ClaimId { get; set; } = string.Empty;
    public string? PendFingerprint { get; set; }
    public List<ClaimEditItem> Edits { get; set; } = new();
    public List<DenialReasonOption> DenialReasons { get; set; } = new();
}

public sealed class ClaimEditItem
{
    public string EditKey { get; set; } = string.Empty;
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string PolicyDescription { get; set; } = string.Empty;
    public string? Message { get; set; }
    public List<int> LineNumbers { get; set; } = new();
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }

    /// <summary>Never, Examiner or Supervisor.</summary>
    public string OverrideLevel { get; set; } = "Never";
    public bool RequiresReason { get; set; }
    public bool RequiresDocumentation { get; set; }
    public bool CanOverride { get; set; }

    public string Lines => LineNumbers.Count == 1 ? $"line {LineNumbers[0]}" : $"lines {string.Join(", ", LineNumbers)}";
}

public sealed class DenialReasonOption
{
    public string GroupCode { get; set; } = "CO";
    public string Carc { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public string Code => $"{GroupCode}-{Carc}";
}

/// <summary>One decision sent with the approval.</summary>
public sealed record EditDecisionInput(
    [property: JsonPropertyName("editKey")] string EditKey,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>A line the reviewer denies on their own judgment, sent with the approval.</summary>
public sealed record LineDenialInput(
    [property: JsonPropertyName("lineNumber")] int LineNumber,
    [property: JsonPropertyName("units")] decimal? Units,
    [property: JsonPropertyName("groupCode")] string GroupCode,
    [property: JsonPropertyName("carc")] string Carc,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// The reviewer's draft while working a pended claim (claim-review-workbench
/// §2.4): nothing is committed until Finish. Every edit starts upheld; the
/// reviewer overrides the ones that don't apply (with a reason) and may deny
/// lines on their own judgment. Finish sends all of it in one approval.
/// </summary>
public sealed class EditReviewDraft
{
    private readonly Dictionary<string, string?> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineDenialInput> _denials = new();

    public EditReviewDraft(ClaimEditsView view) => View = view;

    public ClaimEditsView View { get; }

    public IReadOnlyList<ClaimEditItem> Edits => View.Edits;
    public IReadOnlyList<LineDenialInput> LineDenials => _denials;

    public bool HasAnythingToDecide => View.Edits.Count > 0;

    public bool IsOverridden(ClaimEditItem edit) => _overrides.ContainsKey(edit.EditKey);

    public string? OverrideReason(ClaimEditItem edit) => _overrides.GetValueOrDefault(edit.EditKey);

    /// <summary>Marks an edit overridden (only when the caller may), or back to upheld.</summary>
    public void SetOverride(ClaimEditItem edit, bool overridden)
    {
        if (!overridden) { _overrides.Remove(edit.EditKey); return; }
        if (edit.CanOverride) _overrides[edit.EditKey] = _overrides.GetValueOrDefault(edit.EditKey);
    }

    public void SetOverrideReason(ClaimEditItem edit, string? reason)
    {
        if (_overrides.ContainsKey(edit.EditKey)) _overrides[edit.EditKey] = reason;
    }

    public string? AddLineDenial(int lineNumber, string reasonCode, string reason, decimal? units = null)
    {
        var option = View.DenialReasons.FirstOrDefault(r => r.Code == reasonCode);
        if (option is null) return "Pick a denial reason.";
        if (string.IsNullOrWhiteSpace(reason)) return "Say why the line is denied.";
        if (_denials.Any(d => d.LineNumber == lineNumber)) return $"Line {lineNumber} is already denied.";
        _denials.Add(new LineDenialInput(lineNumber, units, option.GroupCode, option.Carc, reason.Trim()));
        return null;
    }

    public void RemoveLineDenial(int lineNumber) => _denials.RemoveAll(d => d.LineNumber == lineNumber);

    /// <summary>What still has to be fixed before Finish.</summary>
    public IReadOnlyList<string> Problems => View.Edits
        .Where(e => IsOverridden(e) && (e.RequiresReason || e.RequiresDocumentation) && string.IsNullOrWhiteSpace(OverrideReason(e)))
        .Select(e => $"Give a reason for overriding {e.RuleId} on {e.Lines}.")
        .ToList();

    public int OverriddenCount => View.Edits.Count(IsOverridden);
    public int UpheldCount => View.Edits.Count - OverriddenCount;

    /// <summary>
    /// What Finish will do, line by line, in the reviewer's words: upheld
    /// edits deny what they cover, overridden edits stop applying, manual
    /// denials deny their line.
    /// </summary>
    public IReadOnlyList<string> Effects()
    {
        var effects = new List<string>();
        foreach (var edit in View.Edits)
        {
            if (IsOverridden(edit))
            {
                effects.Add($"{edit.RuleId} on {edit.Lines} overridden — it no longer applies.");
            }
            else if (string.Equals(edit.EditType, "Mue", StringComparison.OrdinalIgnoreCase))
            {
                effects.Add($"{edit.RuleId} on {edit.Lines} upheld — units over the limit denied ({Carc(edit)}).");
            }
            else
            {
                effects.Add($"{edit.RuleId} upheld — {edit.Lines} denied ({Carc(edit)}).");
            }
        }
        effects.AddRange(_denials.Select(d =>
            $"Line {d.LineNumber} denied{(d.Units is { } u ? $" ({u} unit(s))" : "")} with {d.GroupCode}-{d.Carc}: {d.Reason}"));
        return effects;
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (View.Edits.Count > 0)
            {
                parts.Add($"{View.Edits.Count} edit{(View.Edits.Count == 1 ? "" : "s")}: {OverriddenCount} overridden, {UpheldCount} upheld");
            }
            if (_denials.Count > 0)
            {
                parts.Add($"{_denials.Count} line{(_denials.Count == 1 ? "" : "s")} denied by you");
            }
            return parts.Count == 0 ? "No edits to decide." : string.Join("; ", parts) + ".";
        }
    }

    /// <summary>The decisions Finish sends: overrides only; every other edit is upheld by the service.</summary>
    public IReadOnlyList<EditDecisionInput> Decisions() => View.Edits
        .Where(IsOverridden)
        .Select(e => new EditDecisionInput(e.EditKey, "Override", OverrideReason(e)?.Trim()))
        .ToList();

    private static string Carc(ClaimEditItem edit) =>
        string.IsNullOrWhiteSpace(edit.SuggestedCarc) ? "CO" : $"CO-{edit.SuggestedCarc}";
}
