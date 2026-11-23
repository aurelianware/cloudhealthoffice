using System.Globalization;
using System.Text;
using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.CodeSets;

/// <summary>A parsed release file: its codes plus the rows it could not read.</summary>
public sealed record ParsedCodeSet(IReadOnlyList<ParsedCode> Codes, int RowsSkipped, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the CMS ICD-10-CM and ICD-10-PCS "order" files (icd10cm_order_2026.txt,
/// icd10pcs_order_2026.txt). Fixed width, one code per line:
/// <code>
///  columns  1-5   order number
///           7-13  code, without the dot, left-justified
///           15    1 = valid for submission on a claim, 0 = header (category) code
///           17-76 short description
///           78-   long description
/// </code>
/// Every code is kept; header codes are stored as not billable, so a claim that
/// uses one gets "not billable" rather than "not in code set".
/// </summary>
public static class Icd10OrderFileParser
{
    public const string Format = "ICD10-ORDER";

    public static async Task<ParsedCodeSet> ParseAsync(Stream source, CancellationToken ct = default)
    {
        var codes = new List<ParsedCode>();
        var errors = new List<string>();
        var skipped = 0;
        var lineNumber = 0;

        using var reader = new StreamReader(source, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parsed = ParseLine(line);
            if (parsed is null)
            {
                skipped++;
                if (errors.Count < 20) errors.Add($"Line {lineNumber}: not in the order-file layout");
                continue;
            }
            codes.Add(parsed);
        }

        return new ParsedCodeSet(codes, skipped, errors);
    }

    internal static ParsedCode? ParseLine(string line)
    {
        if (line.Length < 17) return null;
        if (!line[..5].Trim().All(char.IsAsciiDigit)) return null;

        var code = CodeSetSystems.NormalizeCode(line[6..13]);
        if (code.Length is < 3 or > 7 || !code.All(char.IsAsciiLetterOrDigit)) return null;

        var flag = line[14];
        if (flag is not ('0' or '1')) return null;

        var shortDescription = line.Length > 16 ? line[16..Math.Min(76, line.Length)].Trim() : string.Empty;
        var longDescription = line.Length > 77 ? line[77..].Trim() : string.Empty;
        var display = longDescription.Length > 0 ? longDescription : shortDescription;

        return new ParsedCode(code, display, Billable: flag == '1');
    }
}

/// <summary>
/// Parses a HCPCS Level II release as CSV. Two layouts are accepted, told apart by
/// the header row:
/// <list type="bullet">
///   <item>The CMS quarterly file saved as CSV (columns HCPC, RECID, LONG
///   DESCRIPTION, SHORT DESCRIPTION, ADD DT, ACT EFF DT, TERM DT, ...). Rows with
///   record id 4 or 8 continue the previous code's long description.</item>
///   <item>A plain file with columns code, display, effective_date, end_date.</item>
/// </list>
/// Dates may be yyyyMMdd (as CMS writes them), yyyy-MM-dd or M/d/yyyy.
/// </summary>
public static class HcpcsCsvParser
{
    public const string Format = "HCPCS-CSV";

    private static readonly string[] CodeColumns = ["hcpc", "hcpcs", "code", "hcpcs code"];
    private static readonly string[] RecordIdColumns = ["recid", "rec id", "rec", "record id"];
    private static readonly string[] LongColumns = ["long description", "long_description", "display", "description"];
    private static readonly string[] ShortColumns = ["short description", "short_description"];
    private static readonly string[] AddColumns = ["add dt", "add_dt", "add date", "effective_date", "effective from"];
    private static readonly string[] TermColumns = ["term dt", "term_dt", "termination date", "end_date", "effective to"];

    public static async Task<ParsedCodeSet> ParseAsync(Stream source, CancellationToken ct = default)
    {
        var errors = new List<string>();
        using var reader = new StreamReader(source, Encoding.UTF8);

        var header = await reader.ReadLineAsync(ct);
        if (header is null)
            return new ParsedCodeSet([], 0, ["Empty file"]);

        var columns = SplitCsv(header).Select(h => h.Trim().Trim('﻿').ToLowerInvariant()).ToList();
        int Find(string[] names) => columns.FindIndex(names.Contains);

        var codeIndex = Find(CodeColumns);
        if (codeIndex < 0)
            return new ParsedCodeSet([], 0, ["The header row needs an HCPC or code column"]);
        var recIndex = Find(RecordIdColumns);
        var longIndex = Find(LongColumns);
        var shortIndex = Find(ShortColumns);
        var addIndex = Find(AddColumns);
        var termIndex = Find(TermColumns);

        // Keyed by code so a continuation row extends the right entry and a code
        // listed twice keeps its first row.
        var byCode = new Dictionary<string, (StringBuilder Display, string Short, DateTime? From, DateTime? To)>(StringComparer.Ordinal);
        var order = new List<string>();
        var skipped = 0;
        var lineNumber = 1;
        string? previousCode = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsv(line);
            string Field(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : string.Empty;

            var code = CodeSetSystems.NormalizeCode(Field(codeIndex));
            var recordId = Field(recIndex);
            var isContinuation = recordId is "4" or "8";

            if (isContinuation)
            {
                var continued = code.Length > 0 ? code : previousCode;
                if (continued is not null && byCode.TryGetValue(continued, out var entry))
                {
                    var more = Field(longIndex);
                    if (more.Length > 0) entry.Display.Append(' ').Append(more);
                }
                continue;
            }

            if (code.Length is < 2 or > 5 || !code.All(char.IsAsciiLetterOrDigit))
            {
                skipped++;
                if (errors.Count < 20) errors.Add($"Line {lineNumber}: '{Truncate(code)}' is not a HCPCS code or modifier");
                continue;
            }

            if (!TryDate(Field(addIndex), out var from) || !TryDate(Field(termIndex), out var to))
            {
                skipped++;
                if (errors.Count < 20) errors.Add($"Line {lineNumber}: unreadable date for {code}");
                continue;
            }

            previousCode = code;
            if (byCode.ContainsKey(code)) continue;
            byCode[code] = (new StringBuilder(Field(longIndex)), Field(shortIndex), from, to);
            order.Add(code);
        }

        var codes = order.Select(code =>
        {
            var e = byCode[code];
            var display = e.Display.ToString().Trim();
            return new ParsedCode(code, display.Length > 0 ? display : e.Short, true, e.From, e.To);
        }).ToList();

        return new ParsedCodeSet(codes, skipped, errors);
    }

    /// <summary>Empty is a valid "no date"; anything else must parse.</summary>
    internal static bool TryDate(string value, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"];
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            date = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    /// <summary>Splits one CSV line, honoring double-quoted fields (descriptions hold commas).</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static string Truncate(string s) => s.Length <= 12 ? s : s[..12];
}
