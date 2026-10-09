using System.Text;

namespace ArLegacyPostingReconciliation;

/// <summary>Minimal RFC 4180 CSV: quoted fields, embedded commas, quotes and newlines.</summary>
public static class Csv
{
    public static string Field(string? value)
    {
        value ??= string.Empty;
        // A leading =, +, - or @ is a formula to a spreadsheet: finance opens this file in one.
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string Line(IEnumerable<string?> fields) => string.Join(",", fields.Select(Field));

    /// <summary>Parses the text into rows of fields. A leading byte-order mark is ignored.</summary>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (any || row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (quoted)
            throw new FormatException("CSV ends inside a quoted field");
        if (any || field.Length > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Rows as dictionaries keyed by the header (case-insensitive). Row numbers are 1-based file lines of data (header = 1).</summary>
    public static List<(int RowNumber, Dictionary<string, string> Fields)> ParseWithHeader(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0)
            throw new FormatException("CSV is empty");
        var header = rows[0].Select(h => h.Trim()).ToList();
        var result = new List<(int, Dictionary<string, string>)>();
        for (var r = 1; r < rows.Count; r++)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Count; c++)
                fields[header[c]] = c < rows[r].Count ? rows[r][c] : string.Empty;
            result.Add((r + 1, fields));
        }
        return result;
    }

    /// <summary>Undoes the formula guard <see cref="Field"/> adds.</summary>
    public static string Unguard(string value) =>
        value.Length > 1 && value[0] == '\'' && "=+-@".Contains(value[1]) ? value[1..] : value;
}
