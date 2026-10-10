using System.Globalization;
using System.Text;

namespace AccumulatorReversalKindBackfill;

/// <summary>
/// Strict RFC 4180 CSV: quoted fields with embedded commas, quotes and newlines. Anything that is
/// not well-formed (a quote inside an unquoted field, text after a closing quote, a bare carriage
/// return, a row with more or fewer fields than the header, an unknown or repeated column) is
/// rejected rather than guessed at: the reviewed file decides what is written.
/// </summary>
public static class Csv
{
    /// <summary>
    /// Leading characters a spreadsheet may treat as a formula (or, for tab and carriage return,
    /// strip and then evaluate what follows). A reviewer may open this file in one.
    /// </summary>
    private const string FormulaLeads = "=+-@\t\r";

    public static string Field(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && FormulaLeads.Contains(value[0])
            && !decimal.TryParse(value, NumberStyles.Number & ~NumberStyles.AllowLeadingWhite & ~NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string Line(IEnumerable<string?> fields) => string.Join(",", fields.Select(Field));

    /// <summary>Parses the text into rows of fields. A leading byte-order mark is ignored; blank lines are skipped.</summary>
    /// <exception cref="FormatException">The text is not well-formed CSV; the message names the line.</exception>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var quoted = false;       // inside a quoted field
        var wasQuoted = false;    // the current field was quoted and has closed
        var fieldStarted = false; // the current field has any content (or was quoted)

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            wasQuoted = false;
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add(row);
            row = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; wasQuoted = true; }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] != '\n')
                        throw new FormatException($"line {line}: carriage return without a line feed outside a quoted field");
                    break;
                case '\n':
                    EndRow();
                    line++;
                    break;
                case '"':
                    if (fieldStarted)
                        throw new FormatException($"line {line}: quote character inside an unquoted field (or after a closing quote); quote the whole field and double inner quotes");
                    quoted = true;
                    fieldStarted = true;
                    break;
                default:
                    if (wasQuoted)
                        throw new FormatException($"line {line}: text after a closing quote");
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }
        if (quoted)
            throw new FormatException($"line {line}: the file ends inside a quoted field");
        if (fieldStarted || row.Count > 0)
            EndRow();
        return rows;
    }

    /// <summary>
    /// Rows as dictionaries keyed by the header (exact, case-sensitive column names). Row numbers
    /// count data rows from 2 (the header is 1).
    /// </summary>
    /// <param name="expectedColumns">When given, the header must hold exactly these columns, in any order.</param>
    public static List<(int RowNumber, Dictionary<string, string> Fields)> ParseWithHeader(string text, IReadOnlyCollection<string>? expectedColumns = null)
    {
        var rows = Parse(text);
        if (rows.Count == 0)
            throw new FormatException("CSV is empty");
        var header = rows[0].Select(h => h.Trim()).ToList();
        var repeated = header.GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (repeated.Count > 0)
            throw new FormatException($"column(s) appear more than once: {string.Join(", ", repeated)}");
        if (expectedColumns != null)
        {
            var unexpected = header.Where(h => !expectedColumns.Contains(h)).ToList();
            var missing = expectedColumns.Where(c => !header.Contains(c)).ToList();
            if (unexpected.Count > 0)
                throw new FormatException($"unexpected column(s): {string.Join(", ", unexpected.Select(u => $"'{u}'"))}; use the listing's columns only");
            if (missing.Count > 0)
                throw new FormatException($"CSV lacks column(s): {string.Join(", ", missing)}");
        }

        var result = new List<(int, Dictionary<string, string>)>();
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count != header.Count)
                throw new FormatException($"data row {r + 1} has {rows[r].Count} field(s), the header has {header.Count}; re-export from the listing");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
                fields[header[c]] = rows[r][c];
            result.Add((r + 1, fields));
        }
        return result;
    }

    /// <summary>Undoes the formula guard <see cref="Field"/> adds.</summary>
    public static string Unguard(string value) =>
        value.Length > 1 && value[0] == '\'' && FormulaLeads.Contains(value[1]) ? value[1..] : value;
}
