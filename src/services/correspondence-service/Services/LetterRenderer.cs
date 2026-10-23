using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CloudHealthOffice.PdfLayout;
using CloudHealthOffice.PlanConfiguration;
using CorrespondenceService.Models;

namespace CorrespondenceService.Services;

/// <summary>Everything a letter is rendered from.</summary>
/// <param name="Carrier">
/// The legal entity sending the letter, or null where the tenant's carrier
/// states no letterhead (plan-configuration.md §12.4). A
/// <see cref="TemplateBlockKind.ReturnAddressBlock"/> left blank renders from
/// this; null leaves it empty, so a template that needs a return address fails
/// its required-blocks check rather than going out with a blank one.
/// </param>
public sealed record LetterRenderInput(
    LetterTemplate Template,
    IReadOnlyList<LetterTemplate> AttachmentTemplates,
    IReadOnlyList<string> MissingAttachments,
    IReadOnlyDictionary<string, object?> Model,
    LetterRecipient Recipient,
    string LetterId,
    DateOnly LetterDate,
    DateTime RenderedAt,
    CarrierIdentity? Carrier = null);

public sealed record RenderedBlock(string TemplateId, TemplateBlock Block, string Text, float FontSize);

public sealed record LetterRenderResult(byte[] Pdf, int PageCount, IReadOnlyList<LetterCheck> Checks, IReadOnlyList<RenderedBlock> Blocks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>
/// Fills a template's blocks with merge data and lays them out as a US Letter
/// PDF (SkiaSharp, embedded Noto Sans), attachments appended on new pages,
/// then runs the pre-release checks of §6 step 4. The PDF is always produced
/// so a preview shows what failed; only a letter whose checks all pass is
/// stored.
/// </summary>
public static partial class LetterRenderer
{
    public const string LockedTextUnchanged = "LockedTextUnchanged";
    public const string RequiredBlocksPresent = "RequiredBlocksPresent";
    public const string AttachmentsPresent = "AttachmentsPresent";
    public const string MinimumFontSize = "MinimumFontSize";
    public const string MergeFieldsResolved = "MergeFieldsResolved";
    public const string RecipientAddressComplete = "RecipientAddressComplete";
    public const string FormNotExpired = "FormNotExpired";
    public const string ReadingLevel = "ReadingLevel";

    private const float ParagraphGap = 9f;

    public static LetterRenderResult Render(LetterRenderInput input)
    {
        var page = PageSpec.UsLetter;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var rendered = new List<RenderedBlock>();
        var flow = new List<FlowBlock>();

        void AddTemplate(LetterTemplate template, bool newPage)
        {
            var pageBreakNext = newPage;
            foreach (var block in template.Blocks)
            {
                if (block.Kind == TemplateBlockKind.PageBreak)
                {
                    pageBreakNext = true;
                    continue;
                }

                var text = string.IsNullOrWhiteSpace(block.Text)
                    ? block.Kind switch
                    {
                        TemplateBlockKind.AddressBlock => AddressText(input.Recipient),
                        TemplateBlockKind.ReturnAddressBlock => ReturnAddressText(input.Carrier),
                        _ => MergeFields.Render(block.Text, input.Model, missing),
                    }
                    : MergeFields.Render(block.Text, input.Model, missing);
                var size = SizeOf(template, block);
                rendered.Add(new RenderedBlock(template.TemplateId, block, text, size));
                if (text.Length == 0)
                    continue;

                var style = block.Bold || block.Kind == TemplateBlockKind.Heading ? TextStyle.SemiBold(size) : TextStyle.Regular(size);
                flow.Add(new FlowBlock
                {
                    Lines = TextLayout.Wrap([new TextRun(text, style)], page.ContentWidth),
                    SpaceBefore = block.Kind == TemplateBlockKind.Heading ? ParagraphGap * 1.5f : ParagraphGap,
                    PageBreakBefore = pageBreakNext,
                    KeepTogether = block.Kind is TemplateBlockKind.AddressBlock or TemplateBlockKind.ReturnAddressBlock
                        or TemplateBlockKind.Signature or TemplateBlockKind.Heading,
                });
                pageBreakNext = false;
            }
        }

        AddTemplate(input.Template, newPage: false);
        foreach (var attachment in input.AttachmentTemplates)
            AddTemplate(attachment, newPage: true);

        var placements = FlowDocument.Layout(page, flow);
        var pageCount = placements.Count == 0 ? 1 : placements.Max(p => p.Page) + 1;
        var pdf = FlowDocument.Render(page, flow,
            new PdfInfo(input.Template.Name, "Cloud Health Office correspondence-service", input.RenderedAt),
            footerText: $"Letter {input.LetterId}");

        var checks = new List<LetterCheck>
        {
            CheckLockedText(input.Template, input.AttachmentTemplates),
            CheckRequiredBlocks(input.Template, input.AttachmentTemplates, rendered),
            CheckAttachments(input.MissingAttachments),
            CheckFontSizes(input.Template, input.AttachmentTemplates, placements),
            Check(MergeFieldsResolved, missing.Count == 0, missing.Count == 0 ? null : "Unresolved merge fields: " + string.Join(", ", missing)),
            CheckAddress(input.Recipient),
            CheckFormExpiry(input.Template, input.LetterDate),
        };
        if (input.Template.ReadingLevelMax is { } max)
            checks.Add(CheckReadingLevel(input.Template, rendered, max));

        return new LetterRenderResult(pdf, pageCount, checks, rendered);
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static float SizeOf(LetterTemplate template, TemplateBlock block) =>
        block.FontSize ?? (block.Kind == TemplateBlockKind.Heading ? template.BodyFontPt + 2 : template.BodyFontPt);

    /// <summary>
    /// The sending carrier's name and address, as a letterhead or a return
    /// address. Empty where the carrier states no letterhead name: a letter
    /// headed by a blank line, or by whatever the deployment happens to be
    /// called, is worse than a letter that fails its checks and is not sent.
    /// </summary>
    internal static string ReturnAddressText(CarrierIdentity? carrier)
    {
        if (carrier?.LetterheadName is not { } name) return string.Empty;

        var lines = new List<string> { name };
        var a = carrier.CorrespondenceAddress;
        if (!string.IsNullOrWhiteSpace(a?.Street)) lines.Add(a!.Street!.Trim());
        var cityLine = string.Join(" ", new[] { a?.City is null ? null : a.City.Trim() + ",", a?.State?.Trim(), a?.ZipCode?.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (cityLine.Length > 0) lines.Add(cityLine);
        return string.Join("\n", lines);
    }

    internal static string AddressText(LetterRecipient r)
    {
        var lines = new List<string> { r.Name };
        if (!string.IsNullOrWhiteSpace(r.AddressLine1)) lines.Add(r.AddressLine1!);
        if (!string.IsNullOrWhiteSpace(r.AddressLine2)) lines.Add(r.AddressLine2!);
        var cityLine = string.Join(" ", new[] { r.City is null ? null : r.City + ",", r.State, r.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (cityLine.Length > 0) lines.Add(cityLine);
        return string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private static LetterCheck Check(string name, bool passed, string? detail = null) =>
        new() { Name = name, Passed = passed, Detail = detail };

    private static LetterCheck CheckLockedText(LetterTemplate template, IReadOnlyList<LetterTemplate> attachments)
    {
        var problems = new[] { template }.Concat(attachments)
            .SelectMany(t => t.Blocks.Where(b => b.Locked).Select(b => (t, b)))
            .Where(x => x.b.ApprovedTextHash is null || x.b.ApprovedTextHash != Sha256(x.b.Text))
            .Select(x => $"{x.t.TemplateId}/{x.b.Id}")
            .ToList();
        return Check(LockedTextUnchanged, problems.Count == 0,
            problems.Count == 0 ? null : "Locked text differs from the approved version: " + string.Join(", ", problems));
    }

    private static LetterCheck CheckRequiredBlocks(LetterTemplate template, IReadOnlyList<LetterTemplate> attachments, IReadOnlyList<RenderedBlock> rendered)
    {
        var missing = new[] { template }.Concat(attachments)
            .SelectMany(t => t.RequiredBlocks.Select(id => (t.TemplateId, id)))
            .Where(r => !rendered.Any(b => b.TemplateId == r.TemplateId && b.Block.Id == r.id && b.Text.Length > 0))
            .Select(r => $"{r.TemplateId}/{r.id}")
            .ToList();
        return Check(RequiredBlocksPresent, missing.Count == 0,
            missing.Count == 0 ? null : "Required blocks missing or empty: " + string.Join(", ", missing));
    }

    private static LetterCheck CheckAttachments(IReadOnlyList<string> missing) =>
        Check(AttachmentsPresent, missing.Count == 0,
            missing.Count == 0 ? null : "No approved template for required attachment(s): " + string.Join(", ", missing));

    private static LetterCheck CheckFontSizes(LetterTemplate template, IReadOnlyList<LetterTemplate> attachments, IReadOnlyList<FlowDocument.Placement> placements)
    {
        var min = new[] { template }.Concat(attachments).Max(t => t.MinFontPt);
        var smallest = placements.Count == 0 ? float.MaxValue : placements.Min(p => p.Line.MinSize);
        return Check(MinimumFontSize, smallest >= min,
            smallest >= min ? null : $"Text at {smallest:0.#} pt is below the {min:0.#} pt minimum.");
    }

    private static LetterCheck CheckAddress(LetterRecipient r)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Name)) missing.Add("name");
        if (string.IsNullOrWhiteSpace(r.AddressLine1)) missing.Add("address line 1");
        if (string.IsNullOrWhiteSpace(r.City)) missing.Add("city");
        if (string.IsNullOrWhiteSpace(r.State)) missing.Add("state");
        if (string.IsNullOrWhiteSpace(r.PostalCode) || !PostalCode().IsMatch(r.PostalCode!)) missing.Add("ZIP code");
        return Check(RecipientAddressComplete, missing.Count == 0,
            missing.Count == 0 ? null : "Recipient address incomplete: " + string.Join(", ", missing));
    }

    private static LetterCheck CheckFormExpiry(LetterTemplate template, DateOnly letterDate)
    {
        var expired = template.FormExpiresOn is { } expires && letterDate > expires;
        return Check(FormNotExpired, !expired,
            expired ? $"Form {template.FormNumber} expired on {template.FormExpiresOn:yyyy-MM-dd}." : null);
    }

    private static LetterCheck CheckReadingLevel(LetterTemplate template, IReadOnlyList<RenderedBlock> rendered, double max)
    {
        if (!template.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return Check(ReadingLevel, true, $"Not measured for language '{template.Language}'.");
        var text = string.Join(" ", rendered
            .Where(b => b.TemplateId == template.TemplateId && !b.Block.Locked && b.Block.Kind == TemplateBlockKind.Paragraph)
            .Select(b => b.Text));
        var grade = Readability.FleschKincaidGrade(text);
        return Check(ReadingLevel, grade is null || grade <= max,
            grade is null ? "No plan-authored text to measure." : $"Flesch-Kincaid grade {grade:0.0} (maximum {max:0.0}).");
    }

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex PostalCode();
}

/// <summary>Flesch-Kincaid grade level, for the plan-authored text of English letters.</summary>
public static partial class Readability
{
    public static double? FleschKincaidGrade(string text)
    {
        var words = Word().Matches(text).Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        var sentences = Math.Max(1, Sentence().Matches(text).Count);
        var syllables = words.Sum(Syllables);
        return Math.Round(0.39 * words.Count / sentences + 11.8 * syllables / words.Count - 15.59, 1);
    }

    internal static int Syllables(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length <= 3) return 1;
        if (w.EndsWith("es", StringComparison.Ordinal) || w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
        else if (w.EndsWith('e')) w = w[..^1];
        var groups = Vowels().Matches(w).Count;
        return Math.Max(1, groups);
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"[.!?]+(\s|$)")]
    private static partial Regex Sentence();

    [GeneratedRegex("[aeiouy]+")]
    private static partial Regex Vowels();
}
