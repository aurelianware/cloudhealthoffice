using System.Text;

namespace ClaimsService.Import.Cpcds;

/// <summary>
/// A minimal RFC 4180 reader for CPCDS extracts: comma, pipe or tab
/// delimited, double-quoted fields with doubled quotes inside, CRLF or LF
/// line ends, and newlines inside quoted fields. The delimiter is taken from
/// the header line, because cores differ (QNXT and Facets extract jobs are
/// commonly pipe-delimited; the CARIN sample data is comma-delimited).
/// </summary>
internal static class CpcdsDelimitedReader
{
    private static readonly char[] Candidates = [',', '|', '\t'];

    /// <summary>One physical record, with the 1-based line it started on.</summary>
    internal sealed record Record(int LineNumber, IReadOnlyList<string> Fields);

    /// <summary>
    /// The delimiter the header line uses: whichever candidate appears most
    /// often outside quotes. Comma when the line has none of them.
    /// </summary>
    internal static char DetectDelimiter(string content)
    {
        var end = content.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? content : content[..end];

        var best = ',';
        var bestCount = 0;
        foreach (var candidate in Candidates)
        {
            var count = 0;
            var quoted = false;
            foreach (var ch in header)
            {
                if (ch == '"') quoted = !quoted;
                else if (ch == candidate && !quoted) count++;
            }
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }
        return best;
    }

    internal static IEnumerable<Record> Read(string content, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordStart = 1;
        var i = 0;

        // A leading byte-order mark is not part of the first header name.
        if (content.Length > 0 && content[0] == '﻿') i = 1;

        for (; i < content.Length; i++)
        {
            var ch = content[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (ch == '\n') line++;
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                if (!IsBlank(fields))
                    yield return new Record(recordStart, fields.ToArray());
                fields.Clear();
                line++;
                recordStart = line;
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            if (!IsBlank(fields))
                yield return new Record(recordStart, fields.ToArray());
        }
    }

    private static bool IsBlank(List<string> fields)
        => fields.TrueForAll(string.IsNullOrWhiteSpace);
}
