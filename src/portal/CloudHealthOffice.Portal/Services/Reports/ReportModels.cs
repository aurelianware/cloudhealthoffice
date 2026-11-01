using System.Globalization;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// A standard report a module offers (claims-service <c>GET api/claims/reports</c>):
/// what it shows, the parameters it takes and its columns, in order.
/// </summary>
public sealed class ReportDefinitionView
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public List<ReportParameterView> Parameters { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public string? Regulation { get; set; }

    public bool Takes(string parameter) =>
        Parameters.Any(p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase));
}

public sealed class ReportParameterView
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// One run of a report. Rows are kept as JSON so one table renders any
/// report: a cell is <see cref="Cell"/> of a column named in <see cref="Columns"/>.
/// </summary>
public sealed class ReportRunView
{
    public string ReportId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime AsOf { get; set; }
    public string RunBy { get; set; } = string.Empty;
    public Dictionary<string, string?> Parameters { get; set; } = new();
    public int TotalCount { get; set; }
    public bool Truncated { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, JsonElement>> Items { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    /// <summary>The display text of <paramref name="column"/> (PascalCase, as the catalog names it) in <paramref name="row"/>.</summary>
    public static string Cell(Dictionary<string, JsonElement> row, string column)
    {
        if (!row.TryGetValue(column, out var value)
            && !row.TryGetValue(char.ToLowerInvariant(column[0]) + column[1..], out value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Number when column.StartsWith("Percent", StringComparison.Ordinal) && value.TryGetDecimal(out var share) =>
                (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            JsonValueKind.Number when column.EndsWith("Amount", StringComparison.Ordinal) && value.TryGetDecimal(out var money) =>
                money.ToString("C2", CultureInfo.GetCultureInfo("en-US")),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when column.EndsWith("At", StringComparison.Ordinal) && value.TryGetDateTime(out var at) =>
                at.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => value.GetRawText(),
        };
    }

    /// <summary>"ClaimNumber" → "Claim number"; "BillingProviderNpi" → "Billing provider NPI".</summary>
    public static string Header(string column)
    {
        var words = System.Text.RegularExpressions.Regex.Split(column, "(?<=[a-z0-9])(?=[A-Z])|(?<=[a-z])(?=[0-9])");
        var text = string.Join(" ", words.Select((w, i) => w switch
        {
            "Npi" => "NPI",
            _ when i == 0 => w,
            _ => w.ToLowerInvariant(),
        }));
        return text;
    }
}

/// <summary>A report's CSV, as the service produced it.</summary>
public sealed record ReportFile(string FileName, byte[] Content);

/// <summary>The caller may not run this report (the service answered 403).</summary>
public sealed class ReportForbiddenException : Exception
{
    public ReportForbiddenException(string reportId)
        : base($"You don't have permission to run {reportId}.") { }
}
