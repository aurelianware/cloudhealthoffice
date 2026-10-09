using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Import;

/// <summary>One row of a CMS NCCI PTP (Column 1 / Column 2) edit table.</summary>
public sealed record CmsPtpRow(
    int LineNumber,
    string Column1Code,
    string Column2Code,
    bool ExistedPrior1996,
    DateTime EffectiveDate,
    DateTime? DeletionDate,
    NcciModifierIndicator ModifierIndicator,
    string? Rationale);

/// <summary>One row of a CMS MUE table.</summary>
public sealed record CmsMueRow(
    int LineNumber,
    string ProcedureCode,
    int MaxUnits,
    MueAdjudicationIndicator AdjudicationIndicator,
    string? Rationale);

/// <summary>Rows a parser read plus the lines it could not read.</summary>
public sealed class CmsParseResult<T>
{
    public List<T> Rows { get; } = [];

    /// <summary>"line N: reason" for each rejected line.</summary>
    public List<string> Rejections { get; } = [];
}

/// <summary>
/// Parsers for the public CMS NCCI quarterly files, in the layouts CMS
/// publishes them (https://www.cms.gov/medicare/coding-billing/national-correct-coding-initiative-ncci-edits).
///
/// <para><b>PTP edits</b> (practitioner and outpatient hospital). CMS ships
/// each table as a ZIP of tab-delimited <c>.txt</c> files (and the same data
/// as <c>.xlsx</c>; save a sheet as CSV to use it here). Every data row has
/// seven columns, in this order:</para>
/// <list type="number">
///   <item>Column 1 code</item>
///   <item>Column 2 code</item>
///   <item>"*=in existence prior to 1996" — <c>*</c> or blank</item>
///   <item>Effective Date — <c>YYYYMMDD</c></item>
///   <item>Deletion Date — <c>YYYYMMDD</c>, or <c>*</c> for "no data" (still active)</item>
///   <item>Modifier indicator — 0 not allowed, 1 allowed, 9 not applicable</item>
///   <item>PTP Edit Rationale — free text</item>
/// </list>
///
/// <para><b>MUE tables</b> (practitioner and outpatient hospital). CMS ships
/// each as a ZIP holding a CSV whose header row reads
/// <c>HCPCS/CPT Code, {Setting} Services MUE Values, MUE Adjudication Indicator, MUE Rationale</c>.
/// The indicator cell carries the MAI digit followed by text, e.g.
/// <c>2 Date of Service Edit: Policy</c>. MUE rows carry no dates; the
/// table is effective for the quarter it was published for.</para>
///
/// <para>Both files open with a copyright/disclaimer preamble and header
/// lines. Everything before the first line that parses as a data row is
/// treated as preamble; after that, a non-blank line that does not parse
/// is reported as a rejection rather than silently dropped.</para>
/// </summary>
public static partial class CmsNcciFileParser
{
    public static CmsParseResult<CmsPtpRow> ParsePtp(TextReader reader)
    {
        var result = new CmsParseResult<CmsPtpRow>();
        var seenData = false;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitFields(line);
            var looksLikeData = fields.Count >= 2 && IsCode(fields[0]) && IsCode(fields[1]);
            if (!looksLikeData)
            {
                if (seenData)
                    result.Rejections.Add($"line {lineNumber}: not a PTP row (expected two 5-character codes)");
                continue;
            }

            seenData = true;
            if (fields.Count < 6)
            {
                result.Rejections.Add($"line {lineNumber}: expected at least 6 columns, found {fields.Count}");
                continue;
            }

            if (!TryParseDate(fields[3], out var effective))
            {
                result.Rejections.Add($"line {lineNumber}: invalid effective date '{fields[3]}'");
                continue;
            }

            DateTime? deletion = null;
            var deletionRaw = fields[4];
            if (deletionRaw.Length > 0 && deletionRaw != "*")
            {
                if (!TryParseDate(deletionRaw, out var parsedDeletion))
                {
                    result.Rejections.Add($"line {lineNumber}: invalid deletion date '{deletionRaw}'");
                    continue;
                }
                deletion = parsedDeletion;
            }

            NcciModifierIndicator modifier;
            switch (fields[5])
            {
                case "0": modifier = NcciModifierIndicator.NotAllowed; break;
                case "1": modifier = NcciModifierIndicator.Allowed; break;
                case "9": modifier = NcciModifierIndicator.NotApplicable; break;
                default:
                    result.Rejections.Add($"line {lineNumber}: invalid modifier indicator '{fields[5]}' (expected 0, 1 or 9)");
                    continue;
            }

            result.Rows.Add(new CmsPtpRow(
                lineNumber,
                fields[0].ToUpperInvariant(),
                fields[1].ToUpperInvariant(),
                fields[2] == "*",
                effective,
                deletion,
                modifier,
                fields.Count > 6 && fields[6].Length > 0 ? fields[6] : null));
        }

        return result;
    }

    public static CmsParseResult<CmsMueRow> ParseMue(TextReader reader)
    {
        var result = new CmsParseResult<CmsMueRow>();
        var seenData = false;
        var lineNumber = 0;

        // CMS layout positions; a header row, when found, overrides them.
        int valueCol = 1, maiCol = 2, rationaleCol = 3;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitFields(line);

            if (!seenData && fields.Count >= 3 && fields[0].Contains("HCPCS", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 1; i < fields.Count; i++)
                {
                    if (fields[i].Contains("MUE Value", StringComparison.OrdinalIgnoreCase)) valueCol = i;
                    else if (fields[i].Contains("Adjudication Indicator", StringComparison.OrdinalIgnoreCase)) maiCol = i;
                    else if (fields[i].Contains("Rationale", StringComparison.OrdinalIgnoreCase)) rationaleCol = i;
                }
                continue;
            }

            var looksLikeData = fields.Count > valueCol && IsCode(fields[0]);
            if (!looksLikeData)
            {
                if (seenData)
                    result.Rejections.Add($"line {lineNumber}: not an MUE row (expected a 5-character code)");
                continue;
            }

            seenData = true;
            if (!int.TryParse(fields[valueCol], NumberStyles.None, CultureInfo.InvariantCulture, out var maxUnits))
            {
                result.Rejections.Add($"line {lineNumber}: invalid MUE value '{fields[valueCol]}'");
                continue;
            }

            var maiRaw = fields.Count > maiCol ? fields[maiCol] : string.Empty;
            MueAdjudicationIndicator mai;
            switch (maiRaw.Length > 0 ? maiRaw[0] : ' ')
            {
                case '1': mai = MueAdjudicationIndicator.ClaimLine; break;
                case '2': mai = MueAdjudicationIndicator.DateOfService; break;
                case '3': mai = MueAdjudicationIndicator.DateOfServiceAbsolute; break;
                default:
                    result.Rejections.Add($"line {lineNumber}: invalid MUE adjudication indicator '{maiRaw}' (expected 1, 2 or 3)");
                    continue;
            }

            result.Rows.Add(new CmsMueRow(
                lineNumber,
                fields[0].ToUpperInvariant(),
                maxUnits,
                mai,
                fields.Count > rationaleCol && fields[rationaleCol].Length > 0 ? fields[rationaleCol] : null));
        }

        return result;
    }

    /// <summary>
    /// Splits a tab-delimited line (CMS .txt) or a CSV line with RFC 4180
    /// quoting (CMS .csv / a sheet saved as CSV). Fields are trimmed.
    /// </summary>
    internal static List<string> SplitFields(string line)
    {
        if (line.Contains('\t'))
            return line.Split('\t').Select(f => Unquote(f.Trim())).ToList();

        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\"\"", "\"").Trim() : value;

    private static bool IsCode(string value) => CodeRegex().IsMatch(value);

    private static bool TryParseDate(string value, out DateTime date)
    {
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
        date = default;
        return false;
    }

    // CPT (5 digits, or 4 digits + F/T/U), HCPCS Level II (letter + 4 digits).
    [GeneratedRegex(@"^[A-Za-z0-9]{4}[A-Za-z0-9]$")]
    private static partial Regex CodeRegex();
}
