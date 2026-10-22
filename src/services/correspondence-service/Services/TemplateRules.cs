using System.Text.RegularExpressions;
using CorrespondenceService.Models;

namespace CorrespondenceService.Services;

/// <summary>
/// What a template version must satisfy to be saved as a draft and to be
/// approved (§7). Locked text is the core rule: a Standardized or
/// State-prescribed template is filled, never reworded, so a new version must
/// keep every locked block of the approved version it replaces, word for word.
/// </summary>
public static partial class TemplateRules
{
    public const float FloorFontPt = 12f;
    public const float LargePrintFloorPt = 18f;

    /// <summary>Problems with a draft; empty when it may be saved.</summary>
    public static List<string> ValidateDraft(LetterTemplate t, IReadOnlyCollection<string> knownLetterTypes, LetterTemplate? previousApproved)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Name)) errors.Add("Name is required.");
        if (!knownLetterTypes.Contains(t.LetterTypeCode)) errors.Add($"Letter type '{t.LetterTypeCode}' does not exist.");
        if (string.IsNullOrWhiteSpace(t.Language) || !Language().IsMatch(t.Language)) errors.Add("Language must be an ISO 639-1 code such as 'en' or 'es'.");
        if (t.EffectiveTo is { } to && to <= t.EffectiveFrom) errors.Add("EffectiveTo must be after EffectiveFrom.");
        if (t.Kind is TemplateKind.Standardized or TemplateKind.StatePrescribed && string.IsNullOrWhiteSpace(t.FormNumber))
            errors.Add($"A {t.Kind} template must name its form number.");

        var floor = t.Format == TemplateFormat.LargePrint ? LargePrintFloorPt : FloorFontPt;
        if (t.MinFontPt < floor) errors.Add($"MinFontPt cannot be below {floor} pt for {t.Format} letters.");
        if (t.BodyFontPt < t.MinFontPt) errors.Add("BodyFontPt cannot be below MinFontPt.");

        if (t.Blocks.Count == 0) errors.Add("A template needs at least one block.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in t.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Id) || !BlockId().IsMatch(block.Id))
                errors.Add($"Block id '{block.Id}' must be letters, digits, '-' or '_' (1-64).");
            else if (!ids.Add(block.Id))
                errors.Add($"Block id '{block.Id}' is used twice.");
            if (block.FontSize is { } size && size < t.MinFontPt)
                errors.Add($"Block '{block.Id}' is {size} pt, below the {t.MinFontPt} pt minimum.");
            if (block.Kind != TemplateBlockKind.PageBreak && !MergeFields.TryParse(block.Text, out _, out var parseError))
                errors.Add($"Block '{block.Id}' is not valid Liquid: {parseError}");
            if (block.Locked && block.Kind == TemplateBlockKind.PageBreak)
                errors.Add($"Block '{block.Id}': a page break cannot be locked.");
        }
        foreach (var required in t.RequiredBlocks.Where(r => !ids.Contains(r)))
            errors.Add($"Required block '{required}' is not in the template.");
        foreach (var code in t.Attachments)
        {
            if (code == t.LetterTypeCode) errors.Add("A template cannot attach its own letter type.");
            else if (!knownLetterTypes.Contains(code)) errors.Add($"Attachment letter type '{code}' does not exist.");
        }

        if (previousApproved is not null && t.Kind is TemplateKind.Standardized or TemplateKind.StatePrescribed)
        {
            foreach (var locked in previousApproved.Blocks.Where(b => b.Locked))
            {
                var current = t.Blocks.FirstOrDefault(b => b.Id == locked.Id);
                if (current is null || !current.Locked || current.Text != locked.Text)
                    errors.Add($"Locked block '{locked.Id}' of this {t.Kind} template must stay word for word as approved in version {previousApproved.Version}.");
            }
        }
        return errors;
    }

    /// <summary>Problems that block approval, beyond the draft rules.</summary>
    public static List<string> ValidateApproval(LetterTemplate t, string approverId, bool requireSeparateApprover)
    {
        var errors = new List<string>();
        if (t.Status != TemplateStatus.Draft) errors.Add($"Only a draft can be approved; this version is {t.Status}.");
        if (requireSeparateApprover && string.Equals(t.CreatedBy, approverId, StringComparison.Ordinal))
            errors.Add("A template version must be approved by someone other than its author.");
        if (!t.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) && t.Translation is null)
            errors.Add("A translated template needs the qualified translator who reviewed it (Section 1557).");
        return errors;
    }

    /// <summary>Freezes the approved wording: each locked block's hash is checked before every letter.</summary>
    public static void StampApproval(LetterTemplate t, string approverId, DateTime now)
    {
        foreach (var block in t.Blocks.Where(b => b.Locked))
            block.ApprovedTextHash = LetterRenderer.Sha256(block.Text);
        t.Status = TemplateStatus.Approved;
        t.ApprovedBy = approverId;
        t.ApprovedAt = now;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex BlockId();

    [GeneratedRegex("^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex Language();
}
