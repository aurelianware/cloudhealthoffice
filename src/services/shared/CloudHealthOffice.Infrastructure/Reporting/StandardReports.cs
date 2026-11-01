using System.Globalization;
using System.Text;

namespace CloudHealthOffice.Infrastructure.Reporting;

/// <summary>
/// A module's standard report (docs/architecture/portal-reporting.md, layer
/// 2): what it shows, the permission it needs, its parameters and its fixed,
/// minimum-necessary columns, in order. The portal Reports page renders any
/// report from this definition and its runs.
/// </summary>
public sealed record ReportDefinition(
    string Id,
    string Name,
    string Description,
    string Permission,
    IReadOnlyList<ReportParameter> Parameters,
    IReadOnlyList<string> Columns,
    string? Regulation = null);

public sealed record ReportParameter(string Name, string Type, bool Required, string Description);

/// <summary>One run of a standard report: what was asked, by whom, when, and the rows.</summary>
public sealed class ReportRun<TRow>
{
    public string ReportId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime AsOf { get; init; }
    public string RunBy { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

    /// <summary>Rows that match in all.</summary>
    public int TotalCount { get; init; }

    /// <summary>True when more rows match than were returned.</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<TRow> Items { get; init; } = [];

    /// <summary>What a reader needs to interpret the figures (what is and is not counted).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Builds a run of <paramref name="definition"/> from the matching rows,
    /// keeping at most <paramref name="maxRows"/> of them.
    /// </summary>
    public static ReportRun<TRow> Of(
        ReportDefinition definition, string runBy, DateTime asOf, IReadOnlyCollection<TRow> matching, int maxRows,
        IReadOnlyDictionary<string, string?> parameters, IReadOnlyList<string>? notes = null) => new()
    {
        ReportId = definition.Id,
        Name = definition.Name,
        AsOf = asOf,
        RunBy = runBy,
        Parameters = parameters,
        TotalCount = matching.Count,
        Truncated = matching.Count > maxRows,
        Columns = definition.Columns,
        Items = matching.Take(maxRows).ToList(),
        Notes = notes ?? [],
    };
}

public static class StandardReports
{
    /// <summary>
    /// Rows one interactive run returns. The access logs name every record a
    /// read returned up to this many, so a run never shows records the log
    /// cannot account for. Bigger pulls belong to a data extract.
    /// </summary>
    public const int MaxRows = 1000;

    /// <summary>
    /// Where an endpoint whose response is not an object (a CSV file) puts the
    /// rows it returned, so the access log still names their records.
    /// </summary>
    public const string AuditedRowsItemsKey = "cho.access-audit.rows";

    /// <summary>RFC 4180 CSV of the rows, one column per <paramref name="columns"/> entry (a property of <typeparamref name="TRow"/>), in order.</summary>
    public static string Csv<TRow>(IReadOnlyList<string> columns, IEnumerable<TRow> rows)
    {
        var props = columns.Select(c => typeof(TRow).GetProperty(c)
            ?? throw new InvalidOperationException($"{typeof(TRow).Name} has no column {c}")).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", props.Select(p => CsvField(p.GetValue(row)))));
        return sb.ToString();
    }

    public static string CsvField(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            double x => x.ToString("0.##", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        // Neutralize spreadsheet formulas in text, and quote anything with a delimiter.
        if (value is string && text.Length > 0 && "=+-@".Contains(text[0]))
            text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    public static string CsvFileName(string reportId, DateTime asOf) =>
        $"{reportId}-{asOf.ToUniversalTime():yyyyMMdd'T'HHmm'Z'}.csv";

    /// <summary>
    /// A report period: <paramref name="to"/> defaults to <paramref name="asOf"/>,
    /// <paramref name="from"/> to <paramref name="defaultLength"/> before it;
    /// an error when from is not before to or the period exceeds <paramref name="maxLength"/>.
    /// </summary>
    public static (DateTime From, DateTime To, string? Error) Period(
        DateTime? from, DateTime? to, DateTime asOf, TimeSpan defaultLength, TimeSpan maxLength)
    {
        var toDate = AsUtc(to ?? asOf);
        var fromDate = AsUtc(from ?? toDate - defaultLength);
        if (fromDate >= toDate) return (fromDate, toDate, "from must be before to.");
        if (toDate - fromDate > maxLength) return (fromDate, toDate, $"The period can be at most {maxLength.TotalDays:0} days.");
        return (fromDate, toDate, null);
    }

    /// <summary>
    /// A query date with no offset (<c>2026-03-01</c>) is a UTC date, as report
    /// periods are UTC: read it as one, not as the server's local time.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();

    /// <summary>The first instant of the UTC calendar month holding <paramref name="instant"/>.</summary>
    public static DateTime MonthOf(DateTime instant)
    {
        var u = instant.ToUniversalTime();
        return new DateTime(u.Year, u.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}

/// <summary>Answers a report run as JSON, or as the same rows in CSV when <c>format=csv</c>.</summary>
public static class ReportResults
{
    public static Microsoft.AspNetCore.Mvc.IActionResult Respond<TRow>(
        Microsoft.AspNetCore.Mvc.ControllerBase controller, ReportRun<TRow> run, string? format)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return controller.Ok(run);

        // The access log reads the records from here (the file is not an object it can inspect).
        controller.HttpContext.Items[StandardReports.AuditedRowsItemsKey] = run.Items;
        if (run.Truncated)
            controller.Response.Headers["X-Report-Truncated"] = $"{run.Items.Count} of {run.TotalCount}";
        var csv = StandardReports.Csv(run.Columns, run.Items);
        return controller.File(Encoding.UTF8.GetBytes(csv), "text/csv", StandardReports.CsvFileName(run.ReportId, run.AsOf));
    }
}
