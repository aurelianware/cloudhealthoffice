using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CsvHelper;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace BenefitPlanService.Import.PriorAuthRules.MediCal;

/// <summary>What a Medi-Cal TAR and Non-Benefit List row says about its code.</summary>
[Flags]
public enum MediCalTarRestriction
{
    None = 0,
    /// <summary>"Requires TAR": a Treatment Authorization Request is needed.</summary>
    RequiresTar = 1,
    /// <summary>"Non-Benefit": denied unless an approved TAR shows medical necessity.</summary>
    NonBenefit = 2,
    /// <summary>"Assistant Surgeon services not payable": a billing rule, not an authorization rule.</summary>
    AssistantSurgeonNotPayable = 4,
}

public sealed record MediCalTarRow(string Code, string Description, MediCalTarRestriction Restriction, string List, string RestrictionText);

/// <summary>
/// Reads the Medi-Cal Provider Manual's "TAR and Non-Benefit List" sections
/// (Part 2, one PDF per CPT range, e.g. <c>tarandnoncd6.pdf</c> for codes
/// 60000 thru 69999). Each table row is a code, its description and a
/// "Benefit Restrictions" value: "Requires TAR, Primary Surgeon/Provider",
/// "Non-Benefit", "Assistant Surgeon services not payable", or several joined
/// by semicolons. Descriptions wrap over lines, so a row runs from a line that
/// starts with a code to the next such line.
/// </summary>
public static partial class MediCalTarList
{
    [GeneratedRegex(@"^(?<code>[0-9]{4}[0-9A-Z]|[A-V][0-9]{4})\b\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RowStart();

    [GeneratedRegex(@"TAR and Non-?Benefit List:?\s*Codes\s+(?<from>\S+)\s+thru\s+(?<to>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListTitle();

    [GeneratedRegex(@"^(Part\s+\d|Page updated:|\d+\s*$|Code\s+Description\s+Benefit Restrictions|Legend\b|Symbols used)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageFurniture();

    [GeneratedRegex(@"Requires\s+TAR", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequiresTar();

    [GeneratedRegex(@"Non-?\s?Benefit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonBenefit();

    [GeneratedRegex(@"Assistant\s+Surgeon\s+services\s+not\s+payable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssistantSurgeon();

    // The first restriction phrase in a row's text: everything before it is the description.
    [GeneratedRegex(@"(Requires\s+TAR|Non-?\s?Benefit\b|Assistant\s+Surgeon\s+services)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstRestriction();

    /// <summary>The rows of one list's text, line by line.</summary>
    public static List<MediCalTarRow> Parse(IEnumerable<string> lines, string? fallbackList = null)
    {
        var rows = new List<MediCalTarRow>();
        var list = fallbackList ?? "TAR and Non-Benefit List";
        string? code = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (code is null) return;
            var all = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            var restriction = MediCalTarRestriction.None;
            if (RequiresTar().IsMatch(all)) restriction |= MediCalTarRestriction.RequiresTar;
            if (NonBenefit().IsMatch(all)) restriction |= MediCalTarRestriction.NonBenefit;
            if (AssistantSurgeon().IsMatch(all)) restriction |= MediCalTarRestriction.AssistantSurgeonNotPayable;
            var m = FirstRestriction().Match(all);
            var description = (m.Success ? all[..m.Index] : all).Trim().TrimEnd(';', ',').Trim();
            var restrictionText = m.Success ? all[m.Index..].Trim() : string.Empty;
            rows.Add(new MediCalTarRow(code, description, restriction, list, restrictionText));
            code = null;
            text.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Replace("‹‹", "", StringComparison.Ordinal).Replace("››", "", StringComparison.Ordinal)
                .Replace("<<", "", StringComparison.Ordinal).Replace(">>", "", StringComparison.Ordinal).Trim();
            if (line.Length == 0) continue;

            var title = ListTitle().Match(line);
            if (title.Success)
            {
                Flush();
                list = $"Codes {title.Groups["from"].Value} thru {title.Groups["to"].Value}";
                continue;
            }
            if (PageFurniture().IsMatch(line)) { Flush(); continue; }

            var start = RowStart().Match(line);
            if (start.Success)
            {
                Flush();
                code = start.Groups["code"].Value;
                text.Append(start.Groups["rest"].Value).Append(' ');
            }
            else if (code is not null)
            {
                // A wrapped description or restriction. A heading line between tables
                // (e.g. "Thyroid Gland") ends the row once the row has its restriction.
                if (FirstRestriction().IsMatch(text.ToString()) && !FirstRestriction().IsMatch(line) && LooksLikeHeading(line))
                {
                    Flush();
                    continue;
                }
                text.Append(line).Append(' ');
            }
        }
        Flush();
        return rows;
    }

    /// <summary>Short title-case lines without a period, like "Thyroid Gland" or "Excision".</summary>
    private static bool LooksLikeHeading(string line) =>
        line.Length <= 60 && !line.Contains('.') && !line.Contains(',') && !line.Contains(';')
        && !line.Contains('/') && char.IsUpper(line[0])
        && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => char.IsUpper(w[0]) || w is "and" or "of" or "the" or "or" or "for" or "with" or "on" or "to" or "in");

    /// <summary>The text lines of a PDF, words grouped by baseline and ordered left to right.</summary>
    public static List<string> PdfLines(Stream pdf)
    {
        var lines = new List<string>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            var words = page.GetWords().ToList();
            foreach (var line in words
                         .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.5))
                         .OrderByDescending(g => g.Key))
                lines.Add(string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
        }
        return lines;
    }

    /// <summary>
    /// Every PDF in the uploads (PDFs, or .zip files holding them), in name order.
    /// </summary>
    public static IEnumerable<(string Name, Stream Pdf)> Pdfs(IEnumerable<(string Name, Stream Content)> uploads)
    {
        foreach (var (name, content) in uploads.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            var seekable = content;
            if (!content.CanSeek)
            {
                var copy = new MemoryStream();
                content.CopyTo(copy);
                copy.Position = 0;
                seekable = copy;
            }
            var head = new byte[4];
            var read = seekable.Read(head, 0, 4);
            seekable.Position = 0;
            if (read >= 2 && head[0] == 'P' && head[1] == 'K')
            {
                using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: false);
                foreach (var entry in zip.Entries
                             .Where(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    var ms = new MemoryStream();
                    using (var s = entry.Open()) s.CopyTo(ms);
                    ms.Position = 0;
                    yield return (entry.Name, ms);
                }
            }
            else if (read == 4 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F')
            {
                yield return (name, seekable);
            }
            else
            {
                throw new PriorAuthRuleFormatException($"{name} is not a PDF or a .zip of PDFs.");
            }
        }
    }

    /// <summary>
    /// The rows as a list the prior-auth importer reads: one rule per list file
    /// and kind ("Requires TAR" / "Non-Benefit"). Rows with only billing
    /// restrictions are written with pa_required = N, so the importer counts
    /// and skips them.
    /// </summary>
    public static MemoryStream ToCsv(IEnumerable<MediCalTarRow> rows, bool nonBenefitNeedsTar)
    {
        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var h in new[] { "rule_id", "name", "code", "pa_required", "description" }) csv.WriteField(h);
            csv.NextRecord();
            foreach (var r in rows)
            {
                var tar = r.Restriction.HasFlag(MediCalTarRestriction.RequiresTar);
                var nonBenefit = r.Restriction.HasFlag(MediCalTarRestriction.NonBenefit);
                var required = tar || (nonBenefit && nonBenefitNeedsTar);
                var range = r.List.StartsWith("Codes ", StringComparison.Ordinal) ? r.List["Codes ".Length..] : r.List;
                csv.WriteField((tar ? "tar " : nonBenefit ? "non-benefit " : "no-tar ") + range);
                csv.WriteField(tar ? $"Requires TAR: {r.List}" : nonBenefit ? $"Non-Benefit (TAR for medical necessity): {r.List}" : $"No TAR: {r.List}");
                csv.WriteField(r.Code);
                csv.WriteField(required ? "Y" : "N");
                csv.WriteField(r.Description);
                csv.NextRecord();
            }
        }
        stream.Position = 0;
        return stream;
    }
}

public sealed record MediCalTarImportOptions
{
    public PaLineOfBusiness LineOfBusiness { get; init; } = PaLineOfBusiness.Medicaid;
    public string? Program { get; init; }
    /// <summary>Load Non-Benefit codes as needing a TAR (DHCS covers them only with an approved TAR). Default true.</summary>
    public bool NonBenefitNeedsTar { get; init; } = true;
    public bool KeepExemptions { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public bool IncludeCodes { get; init; }
}

public sealed record MediCalTarFileSummary(string File, string List, int Rows, int RequiresTar, int NonBenefit, int NoTar);

public sealed record MediCalTarImportReport
{
    public required PriorAuthRuleImportReport Import { get; init; }
    public IReadOnlyList<MediCalTarFileSummary> Files { get; init; } = [];
    public int RowsRead { get; init; }
    public int RequiresTar { get; init; }
    public int NonBenefit { get; init; }
    /// <summary>Rows with only billing restrictions (assistant surgeon not payable), not loaded.</summary>
    public int NoTar { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IMediCalTarImportService
{
    Task<MediCalTarImportReport> ImportAsync(
        IEnumerable<(string Name, Stream Content)> uploads, MediCalTarImportOptions options, string tenantId, string? actor,
        CancellationToken ct = default);
}

/// <summary>
/// Loads the Medi-Cal TAR and Non-Benefit List PDFs into the tenant's CA PA
/// rule set through the prior-auth list importer, under the <c>medi-cal</c>
/// namespace (beside the plan's own list; a re-import disables codes that
/// dropped off). See docs/integration/STATE-MANUAL-PA-RULES.md.
/// </summary>
public sealed class MediCalTarImportService(IPriorAuthRuleImportService importer) : IMediCalTarImportService
{
    public const string Namespace = "medi-cal";

    public async Task<MediCalTarImportReport> ImportAsync(
        IEnumerable<(string Name, Stream Content)> uploads, MediCalTarImportOptions options, string tenantId, string? actor,
        CancellationToken ct = default)
    {
        var rows = new List<MediCalTarRow>();
        var files = new List<MediCalTarFileSummary>();
        var warnings = new List<string>();
        foreach (var (name, pdf) in MediCalTarList.Pdfs(uploads))
        {
            ct.ThrowIfCancellationRequested();
            List<string> lines;
            try
            {
                using (pdf) lines = MediCalTarList.PdfLines(pdf);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PriorAuthRuleFormatException($"{name} could not be read as a PDF ({ex.GetType().Name}).");
            }
            var fileRows = MediCalTarList.Parse(lines, Path.GetFileNameWithoutExtension(name));
            if (fileRows.Count == 0)
                warnings.Add($"{name}: no code rows found. Is it a TAR and Non-Benefit List section?");
            var unknown = fileRows.Count(r => r.Restriction == MediCalTarRestriction.None);
            if (unknown > 0)
                warnings.Add($"{name}: {unknown} row(s) have no recognised restriction and are not loaded; check them against the PDF.");
            files.Add(new MediCalTarFileSummary(name, fileRows.FirstOrDefault()?.List ?? "",
                fileRows.Count,
                fileRows.Count(r => r.Restriction.HasFlag(MediCalTarRestriction.RequiresTar)),
                fileRows.Count(r => r.Restriction.HasFlag(MediCalTarRestriction.NonBenefit) && !r.Restriction.HasFlag(MediCalTarRestriction.RequiresTar)),
                fileRows.Count(r => !r.Restriction.HasFlag(MediCalTarRestriction.RequiresTar) && !r.Restriction.HasFlag(MediCalTarRestriction.NonBenefit))));
            rows.AddRange(fileRows);
        }
        if (files.Count == 0) throw new PriorAuthRuleFormatException("Upload the TAR and Non-Benefit List PDFs (or a .zip of them).");

        var duplicates = rows.GroupBy(r => r.Code).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            warnings.Add($"{duplicates.Count} code(s) appear more than once (e.g. {string.Join(", ", duplicates.Take(5))}); " +
                         "a code needs a TAR if any of its rows says so.");
        if (!options.NonBenefitNeedsTar)
            warnings.Add("Non-Benefit codes are not loaded: CRD and claims will not ask for a TAR on them.");

        await using var csv = MediCalTarList.ToCsv(rows, options.NonBenefitNeedsTar);
        var import = await importer.ImportAsync(csv, new PriorAuthRuleImportOptions
        {
            StateCode = "CA",
            LineOfBusiness = options.LineOfBusiness,
            Program = string.IsNullOrWhiteSpace(options.Program) ? null : options.Program.Trim(),
            SourceName = "Medi-Cal TAR and Non-Benefit List",
            KeepExemptions = options.KeepExemptions,
            DryRun = options.DryRun,
            IncludeCodes = options.IncludeCodes,
            RuleNamespace = Namespace,
        }, tenantId, actor, ct);

        return new MediCalTarImportReport
        {
            Import = import,
            Files = files,
            RowsRead = rows.Count,
            RequiresTar = files.Sum(f => f.RequiresTar),
            NonBenefit = files.Sum(f => f.NonBenefit),
            NoTar = files.Sum(f => f.NoTar),
            Warnings = warnings,
        };
    }
}
