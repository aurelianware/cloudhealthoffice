using System.Globalization;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Reports;

/// <summary>
/// The claims module's standard reports (docs/architecture/portal-reporting.md,
/// layer 2): fixed, minimum-necessary columns (no member name, no diagnosis,
/// no member id), named parameters, and a run record of who ran what with
/// which parameters. Every row-level run is written to the claim access log.
/// </summary>
public static class ClaimReportCatalog
{
    public const string Permission = "reports:claims";

    public const string PendedInventory = "claims-pended-inventory";
    public const string OpenAging = "claims-open-aging";
    public const string Denials = "claims-denials";
    public const string ProcessingTimeliness = "claims-processing-timeliness";

    private static readonly ClaimReportParameter Lob =
        new("lineOfBusiness", "LineOfBusiness", false, "Only this line of business.");
    private static readonly ClaimReportParameter From =
        new("from", "date", false, "Start of the period, inclusive (default: 30 days before to).");
    private static readonly ClaimReportParameter To =
        new("to", "date", false, "End of the period, exclusive (default: now).");

    private static readonly string[] InventoryColumns =
    [
        "ClaimId", "ClaimNumber", "Status", "PendCode", "PendedAt", "ReceivedAt", "AgeDays", "AgeBucket",
        "BillingProviderNpi", "LineOfBusiness", "ClaimType", "BilledAmount",
    ];

    public static readonly IReadOnlyList<ClaimReportDefinition> All =
    [
        new(PendedInventory, "Pended claims inventory",
            "Every claim pended now, oldest first, with its pend code and how long it has waited.",
            [Lob, new("pendCode", "string", false, "Only this pend code (UNSPECIFIED: pended with no code).")],
            InventoryColumns, null),
        new(OpenAging, "Open inventory aging",
            "Every claim received and not yet adjudicated, oldest first, with its age bucket (0-15, 16-30, 31-45, 46-60, 61+ days).",
            [Lob, new("minAgeDays", "int", false, "Only claims at least this many full days since receipt.")],
            InventoryColumns, null),
        new(Denials, "Denied claims",
            "Claims denied in [from, to), with the claim-level denial reason.",
            [From, To, Lob, new("denialReasonCode", "string", false, "Only this reason code (UNSPECIFIED: no code).")],
            ["ClaimId", "ClaimNumber", "Status", "DenialReasonCode", "DenialReason", "ReceivedAt", "AdjudicatedAt",
             "BillingProviderNpi", "LineOfBusiness", "ClaimType", "BilledAmount"],
            null),
        new(ProcessingTimeliness, "Claims processing timeliness",
            "Claims received in each month of [from, to): how many were adjudicated (paid or denied) within 30 and 90 days of receipt, against the 90% and 99% standards.",
            [new("from", "date", false, "Start of the receipt period, inclusive (default: 12 months before to)."), To, Lob],
            ["ReceiptMonth", "Received", "Within30Days", "Within90Days", "After90Days", "StillOpen",
             "PercentWithin30", "PercentWithin90", "Meets30DayStandard", "Meets90DayStandard"],
            "42 CFR 447.45(d)(2)-(3), applied to Medicaid managed care by 42 CFR 447.46 and 438.206"),
    ];

    public static ClaimReportDefinition? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed record ClaimReportParameter(string Name, string Type, bool Required, string Description);

public sealed record ClaimReportDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<ClaimReportParameter> Parameters,
    IReadOnlyList<string> Columns,
    string? Regulation)
{
    public string Permission => ClaimReportCatalog.Permission;
}

/// <summary>One run of a standard report: what was asked, by whom, when, and the rows.</summary>
public sealed class ClaimReportRun<TRow>
{
    public string ReportId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime AsOf { get; init; }
    public string RunBy { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

    /// <summary>Rows that match in all.</summary>
    public int TotalCount { get; init; }

    /// <summary>True when more rows match than were returned (<see cref="ClaimReportLimits.MaxRows"/>).</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<TRow> Items { get; init; } = [];

    /// <summary>Notes a reader needs to interpret the figures (what is and is not counted).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static class ClaimReportLimits
{
    /// <summary>
    /// Rows one interactive run returns. The claim access log names every
    /// claim a run returned up to this many, so a run never shows claims the
    /// log cannot account for. Bigger pulls belong to the warehouse extract.
    /// </summary>
    public const int MaxRows = 1000;

    /// <summary>Claims the timeliness report measures in one run.</summary>
    public const int MaxTimelinessClaims = 500_000;
}

/// <summary>A claim in the pended or open inventory.</summary>
public sealed record ClaimInventoryRow(
    string ClaimId,
    string ClaimNumber,
    ClaimStatus Status,
    string? PendCode,
    DateTime? PendedAt,
    DateTime ReceivedAt,
    int AgeDays,
    string AgeBucket,
    string BillingProviderNpi,
    LineOfBusiness LineOfBusiness,
    ClaimType ClaimType,
    decimal BilledAmount);

/// <summary>A denied claim.</summary>
public sealed record ClaimDenialRow(
    string ClaimId,
    string ClaimNumber,
    ClaimStatus Status,
    string? DenialReasonCode,
    string? DenialReason,
    DateTime ReceivedAt,
    DateTime? AdjudicatedAt,
    string BillingProviderNpi,
    LineOfBusiness LineOfBusiness,
    ClaimType ClaimType,
    decimal BilledAmount);

/// <summary>
/// Claims received in one calendar month (UTC) and how quickly they were
/// adjudicated. A standard is null while the month's window is still open
/// (the last claim received in the month has not yet had 30 / 90 days).
/// </summary>
public sealed record ClaimTimelinessRow(
    string ReceiptMonth,
    int Received,
    int Within30Days,
    int Within90Days,
    int After90Days,
    int StillOpen,
    decimal? PercentWithin30,
    decimal? PercentWithin90,
    bool? Meets30DayStandard,
    bool? Meets90DayStandard);

public static class ClaimReportBuilder
{
    /// <summary>42 CFR 447.45(d)(2): 90% of clean claims within 30 days of receipt.</summary>
    public const decimal Standard30 = 0.90m;

    /// <summary>42 CFR 447.45(d)(3): 99% of clean claims within 90 days of receipt.</summary>
    public const decimal Standard90 = 0.99m;

    public static int AgeDays(DateTime receivedAt, DateTime asOf) =>
        Math.Max(0, (int)Math.Floor((asOf - receivedAt.ToUniversalTime()).TotalDays));

    public static string AgeBucket(int ageDays)
    {
        foreach (var (min, max) in ClaimCriteriaFilter.AgingBuckets)
        {
            if (ageDays >= min && (max is null || ageDays <= max))
                return max is null ? $"{min}+" : $"{min}-{max}";
        }
        return "0-15";
    }

    public static ClaimInventoryRow Inventory(Claim c, DateTime asOf)
    {
        var age = AgeDays(c.SubmittedDate, asOf);
        var pended = c.Status == ClaimStatus.Pended;
        return new ClaimInventoryRow(
            c.Id, c.ClaimNumber, c.Status,
            pended ? (string.IsNullOrWhiteSpace(c.PendDetails?.PendCode) ? ClaimCriteria.UnspecifiedPendCode : c.PendDetails!.PendCode) : null,
            pended ? c.PendDetails?.PendedAt : null,
            c.SubmittedDate, age, AgeBucket(age),
            c.BillingProviderNPI, c.LineOfBusiness, c.ClaimType, c.TotalChargeAmount);
    }

    public static ClaimDenialRow Denial(Claim c) => new(
        c.Id, c.ClaimNumber, c.Status,
        string.IsNullOrWhiteSpace(c.AdjudicationResult?.DenialReasonCode) ? ClaimCriteria.UnspecifiedDenialCode : c.AdjudicationResult!.DenialReasonCode,
        c.AdjudicationResult?.DenialReason,
        c.SubmittedDate, c.AdjudicatedDate,
        c.BillingProviderNPI, c.LineOfBusiness, c.ClaimType, c.TotalChargeAmount);

    /// <summary>
    /// One row per receipt month in [from, to). Months with no claims are
    /// listed with zeros so a gap is visible rather than missing.
    /// </summary>
    public static IReadOnlyList<ClaimTimelinessRow> Timeliness(
        IEnumerable<(DateTime Received, DateTime? Adjudicated)> claims, DateTime from, DateTime to, DateTime asOf)
    {
        var months = new SortedDictionary<DateTime, int[]>();
        for (var m = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc); m < to; m = m.AddMonths(1))
            months[m] = new int[5];

        foreach (var (received, adjudicated) in claims)
        {
            var r = received.ToUniversalTime();
            if (r < from || r >= to) continue;
            var key = new DateTime(r.Year, r.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (!months.TryGetValue(key, out var counts)) continue;
            counts[0]++;
            if (adjudicated is not { } a || a.ToUniversalTime() > asOf)
            {
                counts[4]++;
                continue;
            }
            var days = (a.ToUniversalTime() - r).TotalDays;
            if (days <= 30) counts[1]++;
            if (days <= 90) counts[2]++;
            else counts[3]++;
        }

        return months.Select(kv =>
        {
            var month = kv.Key;
            var c = kv.Value;
            // The month's last receipt instant (capped at the period end).
            var lastReceipt = month.AddMonths(1) < to ? month.AddMonths(1) : to;
            decimal? pct30 = c[0] == 0 ? null : Math.Round((decimal)c[1] / c[0], 4);
            decimal? pct90 = c[0] == 0 ? null : Math.Round((decimal)c[2] / c[0], 4);
            bool? meets30 = pct30 is null || asOf < lastReceipt.AddDays(30) ? null : pct30 >= Standard30;
            bool? meets90 = pct90 is null || asOf < lastReceipt.AddDays(90) ? null : pct90 >= Standard90;
            return new ClaimTimelinessRow(
                month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                c[0], c[1], c[2], c[3], c[4], pct30, pct90, meets30, meets90);
        }).ToList();
    }

    /// <summary>RFC 4180 CSV of a run's rows, one column per <paramref name="columns"/> entry, in order.</summary>
    public static string Csv<TRow>(IReadOnlyList<string> columns, IEnumerable<TRow> rows)
    {
        var props = columns.Select(c => typeof(TRow).GetProperty(c)
            ?? throw new InvalidOperationException($"{typeof(TRow).Name} has no column {c}")).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", props.Select(p => Field(p.GetValue(row)))));
        }
        return sb.ToString();
    }

    private static string Field(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        // Quote anything with a delimiter, and neutralize spreadsheet formulas.
        if (text.Length > 0 && "=+-@".Contains(text[0]) && value is string)
            text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
