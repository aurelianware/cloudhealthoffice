using System.Globalization;

namespace PremiumBillingService.StateCapitation;

/// <summary>
/// The two CSV files finance loads for a state: the state's rate sheet and
/// the plan's expected member-months. Comma-separated with a header row;
/// columns are found by name (any order, case-insensitive); fields may be
/// double-quoted. An unreadable row is reported with its line number and the
/// whole file is refused, so a half-loaded rate sheet never prices a month.
///
/// <para>Rates: <c>rate_cell, effective_from, effective_to, pmpm</c>
/// (dates yyyy-MM-dd; effective_to may be empty).</para>
/// <para>Expected: <c>member_id, month, rate_cell</c> and optional
/// <c>days_enrolled</c> (month yyyy-MM).</para>
/// </summary>
public static class StateCapitationCsv
{
    public static IReadOnlyList<StoredStateRate> ReadRates(string content, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        var result = new List<StoredStateRate>();
        foreach (var (line, row) in Rows(content, ["rate_cell", "effective_from", "pmpm"], errs))
        {
            var cell = row["rate_cell"];
            if (cell.Length == 0) { errs.Add($"Line {line}: rate_cell is empty"); continue; }
            if (!TryDate(row["effective_from"], out var from)) { errs.Add($"Line {line}: effective_from '{row["effective_from"]}' is not yyyy-MM-dd"); continue; }
            DateTime? to = null;
            if (row.TryGetValue("effective_to", out var toText) && toText.Length > 0)
            {
                if (!TryDate(toText, out var t)) { errs.Add($"Line {line}: effective_to '{toText}' is not yyyy-MM-dd"); continue; }
                if (t < from) { errs.Add($"Line {line}: effective_to is before effective_from"); continue; }
                to = t;
            }
            if (!decimal.TryParse(row["pmpm"], NumberStyles.Number, CultureInfo.InvariantCulture, out var pmpm) || pmpm < 0)
            { errs.Add($"Line {line}: pmpm '{row["pmpm"]}' is not an amount"); continue; }
            result.Add(new StoredStateRate { RateCell = cell, EffectiveFrom = from, EffectiveTo = to, Pmpm = pmpm });
        }
        errors = errs;
        return errs.Count == 0 ? result : [];
    }

    public static IReadOnlyList<StoredExpectedMonth> ReadExpected(string content, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        var result = new List<StoredExpectedMonth>();
        foreach (var (line, row) in Rows(content, ["member_id", "month", "rate_cell"], errs))
        {
            var member = row["member_id"];
            var cell = row["rate_cell"];
            if (member.Length == 0 || cell.Length == 0) { errs.Add($"Line {line}: member_id and rate_cell are required"); continue; }
            if (!DateTime.TryParseExact(row["month"], "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var month))
            { errs.Add($"Line {line}: month '{row["month"]}' is not yyyy-MM"); continue; }
            int? days = null;
            if (row.TryGetValue("days_enrolled", out var d) && d.Length > 0)
            {
                if (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 31)
                { errs.Add($"Line {line}: days_enrolled '{d}' is not a day count"); continue; }
                days = n;
            }
            result.Add(new StoredExpectedMonth
            {
                MemberId = member,
                Month = DateTime.SpecifyKind(month.Date, DateTimeKind.Utc),
                RateCell = cell,
                DaysEnrolled = days
            });
        }
        errors = errs;
        return errs.Count == 0 ? result : [];
    }

    private static bool TryDate(string text, out DateTime date)
    {
        var ok = DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        return ok;
    }

    private static IEnumerable<(int Line, Dictionary<string, string> Row)> Rows(string content, string[] required, List<string> errors)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var headerIndex = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (headerIndex < 0) { errors.Add("The file is empty"); yield break; }
        var header = Split(lines[headerIndex]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var missing = required.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0) { errors.Add($"Missing column(s): {string.Join(", ", missing)}"); yield break; }

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var fields = Split(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Length; c++)
                row[header[c]] = c < fields.Count ? fields[c].Trim() : string.Empty;
            yield return (i + 1, row);
        }
    }

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
