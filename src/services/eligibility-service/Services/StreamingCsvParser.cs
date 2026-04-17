using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Streaming CSV parser for batch eligibility submissions. Yields one
/// <see cref="BatchEligibilityRow"/> at a time via <see cref="ParseAsync"/>
/// without materializing the full input in memory.
///
/// Supported cells: RFC 4180-style double-quoted fields with embedded
/// commas, escaped quotes (""), and newlines. Unquoted fields stop at
/// the next comma. Blank lines are skipped.
///
/// Required header columns: <c>serviceDate</c> plus one of
/// <c>memberId</c> / <c>subscriberId</c> (case-insensitive).
/// </summary>
public static class StreamingCsvParser
{
    public static async IAsyncEnumerable<BatchEligibilityRow> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var header = await ReadLogicalLineAsync(reader, ct);
        if (header == null) yield break;

        var columns = SplitLogicalLine(header)
            .Select(c => c.Trim().ToLowerInvariant())
            .ToList();

        var memberIdx = columns.IndexOf("memberid");
        var subIdx = columns.IndexOf("subscriberid");
        var dateIdx = columns.IndexOf("servicedate");

        if (memberIdx < 0 && subIdx < 0)
            throw new ArgumentException("CSV must include memberId or subscriberId column");
        if (dateIdx < 0)
            throw new ArgumentException("CSV must include serviceDate column");

        var rowNumber = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await ReadLogicalLineAsync(reader, ct);
            if (line == null) yield break;
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var values = SplitLogicalLine(line);
            var row = new BatchEligibilityRow { RowNumber = rowNumber };

            if (memberIdx >= 0 && memberIdx < values.Count)
                row.MemberId = values[memberIdx]?.Trim();
            if (subIdx >= 0 && subIdx < values.Count)
                row.SubscriberId = values[subIdx]?.Trim();

            if (dateIdx >= values.Count ||
                !DateTime.TryParse(values[dateIdx], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt))
            {
                throw new ArgumentException(
                    $"Row {rowNumber}: serviceDate missing or not a valid ISO-8601 date.");
            }
            row.ServiceDate = dt.Date;

            if (string.IsNullOrWhiteSpace(row.Identifier)) continue;
            yield return row;
        }
    }

    /// <summary>
    /// Reads one logical CSV line, honoring quoted fields that span multiple
    /// physical lines. Returns null at EOF.
    /// </summary>
    internal static async Task<string?> ReadLogicalLineAsync(TextReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var sawAnyChar = false;
        var buffer = new char[1];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer, 0, 1);
            if (read == 0)
                return sawAnyChar ? sb.ToString() : null;

            var ch = buffer[0];
            sawAnyChar = true;

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(ch);
                continue;
            }

            if ((ch == '\r' || ch == '\n') && !inQuotes)
            {
                // Consume a paired \r\n
                if (ch == '\r')
                {
                    var peek = reader.Peek();
                    if (peek == '\n') await reader.ReadAsync(buffer, 0, 1);
                }
                return sb.ToString();
            }

            sb.Append(ch);
        }
    }

    internal static List<string> SplitLogicalLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                // "" inside quoted cell → literal quote
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
