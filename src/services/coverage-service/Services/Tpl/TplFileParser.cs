using System.Globalization;
using System.Text;
using CoverageService.Models.Tpl;

namespace CoverageService.Services.Tpl;

/// <summary>One usable record from a TPL file.</summary>
public sealed record TplFileRow(
    int LineNumber,
    string MemberId,
    string? CarrierCode,
    string CarrierName,
    string? PolicyNumber,
    string? GroupNumber,
    string? PolicyholderName,
    string? StateCoverageCode,
    TplScope Scope,
    bool PayAndChaseOnly,
    bool MeansNoCoverage,
    DateTime EffectiveDate,
    DateTime? EndDate,
    TplRecordAction Action,
    bool ScopeNeedsVerification);

public enum TplRecordAction
{
    /// <summary>Add, or change an existing span (the default).</summary>
    Upsert,
    /// <summary>End the span on its end date (or the file date).</summary>
    End,
    /// <summary>The state removed the record as never valid.</summary>
    Remove,
}

public sealed record TplRowIssue(int LineNumber, string Reason);

public sealed class TplParseResult
{
    public List<TplFileRow> Rows { get; } = new();
    public List<TplRowIssue> Rejected { get; } = new();
    public List<TplRowIssue> Skipped { get; } = new();
    /// <summary>Field to the header it was read from (delimited files).</summary>
    public Dictionary<string, string> ColumnsRead { get; } = new();
    /// <summary>Codes seen whose scope is CHO's reading, not the state's crosswalk.</summary>
    public SortedSet<string> CodesNeedingVerification { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FileError { get; set; }
}

/// <summary>
/// Reads a TPL file with a <see cref="TplFileLayout"/>. Pure: no I/O beyond the
/// text it is handed, so every state's rules are testable without a database.
/// A row that cannot be read with confidence (no member, no start date, a
/// coverage code that maps to nothing) is rejected and reported rather than
/// guessed at, because a wrong scope would deny a claim Medicaid owes.
/// </summary>
public static class TplFileParser
{
    public static TplParseResult Parse(string content, TplFileLayout layout)
    {
        var result = new TplParseResult();
        var lines = SplitLines(content);
        if (lines.Count == 0)
        {
            result.FileError = "The file is empty.";
            return result;
        }

        var codes = new Dictionary<string, TplCodeMapping>(layout.Codes, StringComparer.OrdinalIgnoreCase);

        if (layout.Format == TplFileFormat.FixedWidth)
        {
            var missing = new[] { TplField.MemberId, TplField.EffectiveDate }
                .Where(f => !layout.Fields.TryGetValue(f, out var s) || s.Start < 1 || s.Length < 1)
                .ToList();
            if (missing.Count > 0)
            {
                result.FileError = $"Fixed-width layout has no position for: {string.Join(", ", missing)}.";
                return result;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                string? Get(TplField f) =>
                    layout.Fields.TryGetValue(f, out var s) && s.Start >= 1 && s.Length >= 1
                        ? Slice(line, s.Start, s.Length)
                        : null;
                ReadRow(i + 1, Get, layout, codes, result);
            }
            return result;
        }

        var delimiter = layout.Delimiter switch
        {
            "\\t" or "tab" => '\t',
            { Length: 1 } d => d[0],
            _ => ',',
        };
        var header = SplitDelimited(lines[0], delimiter);
        var index = new Dictionary<TplField, int>();
        foreach (var (field, spec) in layout.Fields)
        {
            var normalisedAliases = spec.Aliases.Select(Normalise).ToHashSet();
            for (var c = 0; c < header.Count; c++)
            {
                if (!normalisedAliases.Contains(Normalise(header[c]))) continue;
                index[field] = c;
                result.ColumnsRead[field.ToString()] = header[c].Trim();
                break;
            }
        }

        var required = new List<TplField> { TplField.MemberId, TplField.EffectiveDate };
        if (!index.ContainsKey(TplField.CoverageCode) && !index.ContainsKey(TplField.Scope))
            required.Add(TplField.CoverageCode);
        var absent = required.Where(f => !index.ContainsKey(f)).ToList();
        if (absent.Count > 0)
        {
            result.FileError =
                $"No column found for {string.Join(", ", absent)} (header: {string.Join(" | ", header.Select(h => h.Trim()))}). " +
                "Rename the column or upload a layout naming it.";
            return result;
        }

        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = SplitDelimited(lines[i], delimiter);
            string? Get(TplField f) =>
                index.TryGetValue(f, out var c) && c < cells.Count ? cells[c] : null;
            ReadRow(i + 1, Get, layout, codes, result);
        }
        return result;
    }

    private static void ReadRow(
        int lineNumber,
        Func<TplField, string?> get,
        TplFileLayout layout,
        Dictionary<string, TplCodeMapping> codes,
        TplParseResult result)
    {
        var memberId = Clean(get(TplField.MemberId));
        if (memberId is null)
        {
            result.Rejected.Add(new(lineNumber, "No member id."));
            return;
        }

        var code = Clean(get(TplField.CoverageCode));
        var scopeText = Clean(get(TplField.Scope));
        TplCodeMapping? mapping = null;
        if (code is not null && codes.TryGetValue(code, out var found))
            mapping = found;

        if (mapping is { Skip: true })
        {
            result.Skipped.Add(new(lineNumber, $"Code '{code}': {mapping.Description}"));
            return;
        }

        var scope = mapping?.Scope ?? TplScope.None;
        if (scopeText is not null)
        {
            if (!TryParseScope(scopeText, out var explicitScope))
            {
                result.Rejected.Add(new(lineNumber, $"Scope '{scopeText}' names no known service category."));
                return;
            }
            scope |= explicitScope;
        }
        else if (mapping is null && code is not null && TryParseScope(code, out var fromCode))
        {
            scope = fromCode;
        }

        var meansNone = mapping?.MeansNoCoverage == true;
        if (scope == TplScope.None && !meansNone)
        {
            result.Rejected.Add(new(lineNumber, code is null
                ? "No coverage code or scope."
                : $"Coverage code '{code}' is not in the {layout.Id} code table; add it to a layout to load these rows."));
            return;
        }

        if (mapping?.ScopeNeedsVerification == true && code is not null)
            result.CodesNeedingVerification.Add(code);

        var effectiveText = Clean(get(TplField.EffectiveDate));
        if (!TryParseDate(effectiveText, layout, out var effective))
        {
            result.Rejected.Add(new(lineNumber, $"Start date '{effectiveText}' is missing or unreadable."));
            return;
        }

        DateTime? end = null;
        var endText = Clean(get(TplField.EndDate));
        if (endText is not null && !layout.OpenEndDates.Contains(endText))
        {
            if (!TryParseDate(endText, layout, out var parsedEnd))
            {
                result.Rejected.Add(new(lineNumber, $"End date '{endText}' is unreadable."));
                return;
            }
            if (parsedEnd < effective)
            {
                result.Rejected.Add(new(lineNumber, $"End date {parsedEnd:yyyy-MM-dd} is before start date {effective:yyyy-MM-dd}."));
                return;
            }
            end = parsedEnd;
        }

        var actionText = Clean(get(TplField.RecordAction));
        TplRecordAction action;
        switch (actionText?.ToUpperInvariant())
        {
            case null or "A" or "ADD" or "C" or "CHANGE" or "U" or "UPDATE" or "001" or "021":
                action = TplRecordAction.Upsert;
                break;
            case "T" or "TERM" or "E" or "END" or "D" or "DELETE" or "024":
                action = TplRecordAction.End;
                break;
            case "R" or "REMOVE" or "VOID" or "X":
                action = TplRecordAction.Remove;
                break;
            default:
                result.Rejected.Add(new(lineNumber, $"Record action '{actionText}' is not add, change, end or remove."));
                return;
        }

        var carrierCode = Clean(get(TplField.CarrierCode));
        var carrierName = Clean(get(TplField.CarrierName));
        if (carrierName is null && carrierCode is null && !meansNone)
        {
            result.Rejected.Add(new(lineNumber, "No carrier name or carrier code."));
            return;
        }

        result.Rows.Add(new TplFileRow(
            lineNumber,
            memberId,
            carrierCode,
            carrierName ?? carrierCode ?? string.Empty,
            Clean(get(TplField.PolicyNumber)),
            Clean(get(TplField.GroupNumber)),
            Clean(get(TplField.PolicyholderName)),
            code,
            scope,
            mapping?.PayAndChaseOnly == true,
            meansNone,
            effective,
            end,
            action,
            mapping?.ScopeNeedsVerification == true));
    }

    private static readonly Dictionary<string, TplScope> ScopeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comprehensive"] = TplScope.Comprehensive,
        ["medical"] = TplScope.Medical,
        ["majormedical"] = TplScope.Medical,
        ["hospital"] = TplScope.Hospital,
        ["inpatient"] = TplScope.Hospital,
        ["outpatient"] = TplScope.Hospital,
        ["professional"] = TplScope.Professional,
        ["physician"] = TplScope.Professional,
        ["pharmacy"] = TplScope.Pharmacy,
        ["drug"] = TplScope.Pharmacy,
        ["rx"] = TplScope.Pharmacy,
        ["dental"] = TplScope.Dental,
        ["vision"] = TplScope.Vision,
        ["behavioralhealth"] = TplScope.BehavioralHealth,
        ["mentalhealth"] = TplScope.BehavioralHealth,
        ["longtermcare"] = TplScope.LongTermCare,
        ["ltc"] = TplScope.LongTermCare,
        ["dme"] = TplScope.Dme,
        ["casualty"] = TplScope.Casualty,
        ["medicareparta"] = TplScope.MedicarePartA,
        ["medicarea"] = TplScope.MedicarePartA,
        ["medicarepartb"] = TplScope.MedicarePartB,
        ["medicareb"] = TplScope.MedicarePartB,
        ["medicarepartc"] = TplScope.MedicarePartC | TplScope.Medical,
        ["medicarec"] = TplScope.MedicarePartC | TplScope.Medical,
        ["medicareadvantage"] = TplScope.MedicarePartC | TplScope.Medical,
        ["medicarepartd"] = TplScope.MedicarePartD | TplScope.Pharmacy,
        ["medicared"] = TplScope.MedicarePartD | TplScope.Pharmacy,
    };

    /// <summary>Reads scope names separated by <c>; , |</c> or <c>+</c>.
    /// Every part must be known, so a typo is rejected rather than ignored.</summary>
    public static bool TryParseScope(string text, out TplScope scope)
    {
        scope = TplScope.None;
        var parts = text.Split(new[] { ';', ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            var key = new string(part.Where(char.IsLetter).ToArray());
            if (!ScopeNames.TryGetValue(key, out var s) && !Enum.TryParse(key, ignoreCase: true, out s))
                return false;
            scope |= s;
        }
        return scope != TplScope.None;
    }

    private static bool TryParseDate(string? text, TplFileLayout layout, out DateTime date)
    {
        date = default;
        if (text is null) return false;
        if (!DateTime.TryParseExact(text, layout.DateFormats.ToArray(), CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return false;
        date = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        return true;
    }

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Normalise(string header) =>
        new(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Slice(string line, int start, int length)
    {
        var from = start - 1;
        if (from >= line.Length) return string.Empty;
        return line.Substring(from, Math.Min(length, line.Length - from));
    }

    private static List<string> SplitLines(string content)
    {
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>RFC 4180 split of one line: quoted cells may hold the
    /// delimiter and doubled quotes.</summary>
    public static List<string> SplitDelimited(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells;
    }
}
