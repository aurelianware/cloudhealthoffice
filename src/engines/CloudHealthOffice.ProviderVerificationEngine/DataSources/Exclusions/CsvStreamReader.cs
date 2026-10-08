namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Text;

/// <summary>
/// Streaming RFC 4180 CSV reader: quoted fields may contain commas, doubled
/// quotes ("") and line breaks; CRLF, LF and CR line endings are accepted.
/// Rows are yielded lazily so an 80k–200k row exclusion file is never held
/// in memory as text.
/// </summary>
public static class CsvStreamReader
{
    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        int ch;

        while ((ch = reader.Read()) != -1)
        {
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !fieldStarted:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    break;
                case '\r':
                case '\n':
                    if (c == '\r' && reader.Peek() == '\n')
                        reader.Read();
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    if (!(fields.Count == 1 && fields[0].Length == 0))
                        yield return fields.ToArray();
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0 || fieldStarted)
        {
            fields.Add(field.ToString());
            if (!(fields.Count == 1 && fields[0].Length == 0))
                yield return fields.ToArray();
        }
    }
}

/// <summary>
/// Maps columns by header name rather than position, so a publisher adding,
/// removing or reordering columns does not silently shift data between
/// fields. Header names are compared case-, space- and punctuation-
/// insensitively ("State / Province" == "STATEPROVINCE").
/// </summary>
public sealed class CsvHeaderMap
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public CsvHeaderMap(IReadOnlyList<string> header)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var key = Key(header[i]);
            if (key.Length > 0)
                _index.TryAdd(key, i);
        }
    }

    public bool Has(params string[] names) => names.Any(n => _index.ContainsKey(Key(n)));

    /// <summary>Value of the first present column among <paramref name="names"/>, trimmed; null when absent/empty.</summary>
    public string? Get(string[] row, params string[] names)
    {
        foreach (var name in names)
        {
            if (_index.TryGetValue(Key(name), out var i))
            {
                if (i >= row.Length)
                    return null;
                var value = row[i].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.TrimStart('﻿'))
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
