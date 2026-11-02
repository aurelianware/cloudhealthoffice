using System.Globalization;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Reporting;

namespace ClaimsService.Finance;

/// <summary>The eight STAR risk groups of HHSC's STAR Claims Lag Report (UMCM 5.6.2.11), in the template's order.</summary>
public static class TexasStarRiskGroups
{
    public static IReadOnlyList<(string Code, string Title)> All { get; } =
    [
        ("UNDER-1", "Under Age 1 Child"),
        ("AGE-1-5", "Age 1-5 Child"),
        ("AGE-6-14", "Age 6-14 Child"),
        ("AGE-15-18", "Age 15-18 Child"),
        ("AGE-19-20", "Age 19-20 Child"),
        ("AA-PCA", "Adoption Assistance / Permanency Care Assistance (AA/PCA)"),
        ("TANF-ADULT", "TANF Adult"),
        ("PREGNANT-WOMAN", "Pregnant Woman"),
    ];

    /// <summary>The code for a group given by code or by the template's title; null if neither.</summary>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return All.FirstOrDefault(g => string.Equals(g.Code, v, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(g.Title, v, StringComparison.OrdinalIgnoreCase)).Code;
    }

    public static string Title(string code) => All.First(g => g.Code == code).Title;
}

/// <summary>A member's rate cell for one month, as loaded with the state's expected member-months.</summary>
public sealed record ExpectedRateCell(string MemberId, DateOnly Month, string RateCell);

public sealed class TexasClaimsLagRequest
{
    /// <summary>State fiscal year, named for the year it ends (SFY 2027: September 2026 to August 2027).</summary>
    public int StateFiscalYear { get; set; }

    /// <summary>SFY quarter, 1 (Sep-Nov) to 4 (Jun-Aug). Payments through the quarter's last month are counted.</summary>
    public int Quarter { get; set; }

    /// <summary>First month of service (yyyy-MM). Default: the first month of the fiscal year.</summary>
    public string? IncurredFrom { get; set; }

    /// <summary>
    /// Which risk group each rate cell belongs to (code such as TANF-ADULT, or
    /// the template's section title). Members in rate cells not listed here
    /// (other programs or service areas) are left out and counted.
    /// </summary>
    public Dictionary<string, string> RateCellRiskGroups { get; set; } = new();

    /// <summary>The MCO's official name in Texas (required for the workbook).</summary>
    public string? Contractor { get; set; }

    /// <summary>The service area, e.g. Bexar (required for the workbook).</summary>
    public string? ServiceArea { get; set; }

    /// <summary>Default: today in Texas.</summary>
    public DateOnly? SubmissionDate { get; set; }

    public List<string> Validate(bool forWorkbook, out Dictionary<string, string> groups)
    {
        var errors = new List<string>();
        groups = new Dictionary<string, string>(StringComparer.Ordinal);
        if (StateFiscalYear is < 2000 or > 2100) errors.Add("stateFiscalYear must be a year, e.g. 2027");
        if (Quarter is < 1 or > 4) errors.Add("quarter must be 1 to 4");
        if (IncurredFrom != null && !TexasClaimsLagReport.TryMonth(IncurredFrom, out _)) errors.Add("incurredFrom must be yyyy-MM");
        if (RateCellRiskGroups.Count == 0) errors.Add("rateCellRiskGroups must map at least one rate cell to a STAR risk group");
        foreach (var (cell, group) in RateCellRiskGroups)
        {
            if (TexasStarRiskGroups.Resolve(group) is { } code) groups[cell.Trim()] = code;
            else errors.Add($"Rate cell '{cell}': '{group}' is not a STAR risk group ({string.Join(", ", TexasStarRiskGroups.All.Select(g => g.Code))})");
        }
        foreach (var (name, value) in new[] { ("contractor", Contractor), ("serviceArea", ServiceArea) })
        {
            if (forWorkbook && string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required for the workbook");
            if (value is { Length: > 200 }) errors.Add($"{name} is longer than 200 characters");
            if (value != null && Regex.IsMatch(value, @"\p{Cc}")) errors.Add($"{name} has control characters");
        }
        return errors;
    }
}

public sealed record TexasLagCell(DateOnly IncurredMonth, DateOnly PaidMonth, decimal Amount);

public sealed record TexasLagGroup(string Code, string Title, IReadOnlyList<TexasLagCell> Cells, decimal Total);

/// <summary>Money left out of the report, by reason.</summary>
public sealed record TexasLagExclusion(string Reason, int Claims, decimal Amount, IReadOnlyList<string> Examples);

public sealed record TexasClaimsLag(
    int StateFiscalYear,
    int Quarter,
    DateOnly IncurredFrom,
    DateOnly Through,
    IReadOnlyList<TexasLagGroup> Groups,
    decimal Total,
    IReadOnlyList<TexasLagExclusion> Excluded,
    IReadOnlyList<string> Notes);

/// <summary>
/// HHSC's STAR Claims Lag Report (UMCM 5.6.2.11, instructions 5.6.2.4): for
/// each STAR risk group, plan payments by month of service (down) and month
/// of payment (across). A later adjustment or void is entered in the month
/// it happened, against the claim's original month of service, which is how
/// <see cref="LagTriangle.Increments"/> already counts it. A claim's risk
/// group is its member's rate cell in the month of service.
/// </summary>
public static class TexasClaimsLagReport
{
    public const string Sheet = "STAR Claims Lag Report";

    public const string NoMember = "No member on the claim";
    public const string NotEnrolled = "Member has no expected member-month (rate cell) for the month of service";
    public const string UnmappedRateCell = "Member's rate cell is not mapped to a STAR risk group (another program or service area)";

    public static bool TryMonth(string? value, out DateOnly month) =>
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);

    public static DateOnly FiscalYearStart(int sfy) => new(sfy - 1, 9, 1);

    /// <summary>The last month of an SFY quarter: Q1 November, Q2 February, Q3 May, Q4 August.</summary>
    public static DateOnly QuarterEnd(int sfy, int quarter) => FiscalYearStart(sfy).AddMonths(quarter * 3 - 1);

    public static TexasClaimsLag Build(
        int sfy, int quarter, DateOnly incurredFrom,
        IEnumerable<PaidIncrement> increments,
        IEnumerable<ExpectedRateCell> rateCells,
        IReadOnlyDictionary<string, string> rateCellGroups)
    {
        var through = QuarterEnd(sfy, quarter);
        var cellOf = new Dictionary<(string, DateOnly), string>();
        foreach (var r in rateCells) cellOf[(r.MemberId, r.Month)] = r.RateCell.Trim();

        var sums = new Dictionary<(string Group, DateOnly Incurred, DateOnly Paid), decimal>();
        var excluded = new Dictionary<string, (HashSet<string> Claims, decimal Amount, SortedSet<string> Examples)>();
        var notes = new List<string>();
        var paidBeforeService = 0;

        foreach (var inc in increments)
        {
            if (inc.IncurredMonth < incurredFrom || inc.IncurredMonth > through || inc.PaidMonth > through) continue;

            string? reason = null, example = null, group = null;
            if (string.IsNullOrEmpty(inc.MemberId)) reason = NoMember;
            else if (!cellOf.TryGetValue((inc.MemberId, inc.IncurredMonth), out var cell)) reason = NotEnrolled;
            else if (!rateCellGroups.TryGetValue(cell, out group)) { reason = UnmappedRateCell; example = cell; }

            if (reason != null)
            {
                if (!excluded.TryGetValue(reason, out var e)) excluded[reason] = e = ([], 0m, new SortedSet<string>(StringComparer.Ordinal));
                e.Claims.Add(inc.ClaimNumber);
                if (example != null && e.Examples.Count < 20) e.Examples.Add(example);
                excluded[reason] = (e.Claims, e.Amount + inc.Amount, e.Examples);
                continue;
            }

            var paid = inc.PaidMonth;
            if (paid < inc.IncurredMonth) { paid = inc.IncurredMonth; paidBeforeService++; }
            sums[(group!, inc.IncurredMonth, paid)] = sums.GetValueOrDefault((group!, inc.IncurredMonth, paid)) + inc.Amount;
        }

        if (paidBeforeService > 0)
            notes.Add($"{paidBeforeService} payment(s) dated before the month of service were entered in the month of service");
        if (excluded.Count > 0)
            notes.Add("Some paid claims were left out (see excluded); check the rate cell mapping and the expected member-months loaded");

        var groups = TexasStarRiskGroups.All.Select(g =>
        {
            var cells = sums.Where(s => s.Key.Group == g.Code && s.Value != 0m)
                .OrderBy(s => s.Key.Incurred).ThenBy(s => s.Key.Paid)
                .Select(s => new TexasLagCell(s.Key.Incurred, s.Key.Paid, s.Value)).ToList();
            return new TexasLagGroup(g.Code, g.Title, cells, cells.Sum(c => c.Amount));
        }).ToList();

        return new TexasClaimsLag(sfy, quarter, incurredFrom, through, groups, groups.Sum(g => g.Total),
            excluded.Select(e => new TexasLagExclusion(e.Key, e.Value.Claims.Count, e.Value.Amount, e.Value.Examples.ToList()))
                .OrderByDescending(e => e.Amount).ToList(),
            notes);
    }

    /// <summary>Where each section, month of service and month of payment is on the template.</summary>
    public sealed record Layout(
        IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, int>> IncurredRows,
        IReadOnlyDictionary<DateOnly, string> PaidColumns);

    private static readonly Regex IncurredLabel = new(@"^Month Incurred (?<m>[A-Za-z]{3}) (?<y>\d{4})$", RegexOptions.CultureInvariant);
    private static readonly Regex PaidLabel = new(@"^Month Paid (?<m>[A-Za-z]{3}) (?<y>\d{4})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the template's own labels rather than assuming fixed rows: HHSC
    /// extends the months every year. Problems are returned when a section
    /// is missing or a header is not where the instructions put it.
    /// </summary>
    public static (Layout? Layout, List<string> Problems) ReadLayout(XlsxTemplate workbook)
    {
        if (!workbook.SheetNames.Contains(Sheet))
            return (null, [$"The workbook has no '{Sheet}' sheet; upload HHSC's UMCM 5.6.2.11 STAR Claims Lag Report"]);

        var problems = new List<string>();
        foreach (var (cell, label) in new[] { ("A2", "Contractor:"), ("A3", "Service Area:"), ("A4", "SFY:"), ("A5", "Quarter:"), ("A6", "Date Submitted:") })
            if (!string.Equals(Normalize(workbook.Text(Sheet, cell)), label, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Sheet}!{cell} should be \"{label}\"");

        var columnA = workbook.ColumnTexts(Sheet, "A");
        var titleRows = new Dictionary<string, int>();
        foreach (var (code, title) in TexasStarRiskGroups.All)
        {
            var row = columnA.Where(kv => string.Equals(Normalize(kv.Value), title, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).DefaultIfEmpty(0).Min();
            if (row == 0) problems.Add($"No '{title}' section in column A");
            else titleRows[code] = row;
        }
        if (problems.Count > 0) return (null, problems);

        var ordered = titleRows.OrderBy(t => t.Value).ToList();
        var incurred = new Dictionary<string, IReadOnlyDictionary<DateOnly, int>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = ordered[i].Value;
            var rows = new Dictionary<DateOnly, int>();
            foreach (var (row, text) in columnA.Where(kv => kv.Key > start).OrderBy(kv => kv.Key))
            {
                var t = Normalize(text);
                if (t.EndsWith("Subtotal", StringComparison.OrdinalIgnoreCase)) break;
                if (IncurredLabel.Match(t) is { Success: true } m && ParseMonth(m) is { } month) rows[month] = row;
            }
            if (rows.Count == 0) problems.Add($"The '{TexasStarRiskGroups.Title(ordered[i].Key)}' section has no 'Month Incurred' rows");
            incurred[ordered[i].Key] = rows;
        }

        var paid = new Dictionary<DateOnly, string>();
        foreach (var (column, text) in workbook.RowTexts(Sheet, ordered[0].Value + 1))
            if (PaidLabel.Match(Normalize(text)) is { Success: true } m && ParseMonth(m) is { } month) paid[month] = column;
        if (paid.Count == 0) problems.Add($"Row {ordered[0].Value + 1} has no 'Month Paid' columns");

        return problems.Count > 0 ? (null, problems) : (new Layout(incurred, paid), problems);
    }

    /// <summary>The cells the report needs that the template does not have (months outside its range).</summary>
    public static List<string> Missing(Layout layout, TexasClaimsLag lag)
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var g in lag.Groups)
            foreach (var c in g.Cells)
            {
                if (!layout.IncurredRows[g.Code].ContainsKey(c.IncurredMonth)) problems.Add($"The template has no 'Month Incurred {c.IncurredMonth:MMM yyyy}' row");
                if (!layout.PaidColumns.ContainsKey(c.PaidMonth)) problems.Add($"The template has no 'Month Paid {c.PaidMonth:MMM yyyy}' column");
            }
        return problems.ToList();
    }

    public static byte[] Fill(XlsxTemplate workbook, Layout layout, TexasClaimsLag lag, string contractor, string serviceArea, DateOnly submissionDate)
    {
        workbook.SetText(Sheet, "B2", contractor.Trim());
        workbook.SetText(Sheet, "B3", serviceArea.Trim());
        workbook.SetNumber(Sheet, "B4", lag.StateFiscalYear);
        workbook.SetText(Sheet, "B5", $"Q{lag.Quarter}");
        // B6 is not date-formatted on HHSC's template, so the date goes in as
        // text, the way 5.6.2.4 writes it (12/30/2016).
        workbook.SetText(Sheet, "B6", submissionDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
        foreach (var g in lag.Groups)
            foreach (var c in g.Cells)
                workbook.SetNumber(Sheet, layout.PaidColumns[c.PaidMonth] + layout.IncurredRows[g.Code][c.IncurredMonth].ToString(CultureInfo.InvariantCulture), c.Amount);
        // The subtotals and the Total section are the template's formulas; the
        // workbook recalculates on open, and stale cached zeros are dropped.
        workbook.ClearCachedValues(Sheet);
        return workbook.Save();
    }

    private static DateOnly? ParseMonth(Match m) =>
        DateTime.TryParseExact($"{m.Groups["m"].Value} {m.Groups["y"].Value}", "MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateOnly(d.Year, d.Month, 1)
            : null;

    private static string Normalize(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
