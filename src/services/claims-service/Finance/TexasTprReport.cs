using System.Globalization;
using System.Text.RegularExpressions;
using ClaimsService.Models.Tpl;
using CloudHealthOffice.Infrastructure.Reporting;

namespace ClaimsService.Finance;

/// <summary>The program worksheets of HHSC's TPL/TPR Report (UMCM 5.3.4.1), by sheet name.</summary>
public static class TexasTprPrograms
{
    public static IReadOnlyList<string> All { get; } =
        ["CHIP", "Dental-CHIP", "Dental-Medicaid", "STAR", "STAR Health", "STAR Kids", "STAR+PLUS"];

    public static string? Resolve(string? value) =>
        All.FirstOrDefault(p => string.Equals(p, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The five reporting lines of the TPL/TPR Report, as UMCM 5.3.4.2 defines them.</summary>
public enum TexasTprLine
{
    /// <summary>Cost Avoidance, Denied Claims: the Medicaid allowable denied because other insurance was identified, by month adjudicated.</summary>
    DeniedClaims,
    /// <summary>Other Insurance Credits: other insurers' payments applied to the claim as received.</summary>
    OtherInsuranceCredits,
    /// <summary>Direct Savings, Other Insurance Recoveries: post-payment recoveries from other carriers, by month received.</summary>
    OtherInsuranceRecoveries,
    /// <summary>Direct Savings, Provider Refunds and Recoupments: money back from providers because other insurance paid.</summary>
    ProviderRefundsAndRecoupments,
    /// <summary>Direct Savings, Tort Recoveries: settlements net of fees and costs (entered by finance).</summary>
    TortRecoveries,
}

public sealed class TexasTprPlanCode
{
    /// <summary>The program worksheet: CHIP, Dental-CHIP, Dental-Medicaid, STAR, STAR Health, STAR Kids or STAR+PLUS.</summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>HHSC plan code.</summary>
    public string PlanCode { get; set; } = string.Empty;
}

public sealed class TexasTprTortRecovery
{
    public string Program { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Month received (yyyy-MM).</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Net of contingency and attorney's fees and recovery costs.</summary>
    public decimal Amount { get; set; }
}

public sealed class TexasTprRequest
{
    /// <summary>State fiscal year, named for the year it ends.</summary>
    public int StateFiscalYear { get; set; }

    /// <summary>1 to 4. The report carries every month of the SFY through the quarter's last month.</summary>
    public int Quarter { get; set; }

    /// <summary>The MCO's official name in Texas (required for the workbook).</summary>
    public string? Contractor { get; set; }

    /// <summary>Three-character MCO code for HHSC's file name (MCOTPRQXXMMYY). Optional.</summary>
    public string? McoCode { get; set; }

    /// <summary>Default: today in Texas.</summary>
    public DateOnly? SubmissionDate { get; set; }

    /// <summary>Program and plan code for each rate cell loaded with the expected member-months.</summary>
    public Dictionary<string, TexasTprPlanCode> RateCellPlanCodes { get; set; } = new();

    public List<TexasTprTortRecovery> TortRecoveries { get; set; } = new();

    public List<string> Validate(bool forWorkbook, out Dictionary<string, (string Program, string PlanCode)> planCodes)
    {
        var errors = new List<string>();
        planCodes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (StateFiscalYear is < 2000 or > 2100) errors.Add("stateFiscalYear must be a year, e.g. 2027");
        if (Quarter is < 1 or > 4) errors.Add("quarter must be 1 to 4");
        if (RateCellPlanCodes.Count == 0) errors.Add("rateCellPlanCodes must map at least one rate cell to a program and plan code");
        foreach (var (cell, target) in RateCellPlanCodes)
        {
            var program = TexasTprPrograms.Resolve(target?.Program);
            if (program is null) errors.Add($"Rate cell '{cell}': program must be one of {string.Join(", ", TexasTprPrograms.All)}");
            else if (!ValidCode(target!.PlanCode)) errors.Add($"Rate cell '{cell}': planCode must be 1 to 10 letters or digits");
            else planCodes[cell.Trim()] = (program, target.PlanCode.Trim());
        }
        foreach (var t in TortRecoveries)
        {
            if (TexasTprPrograms.Resolve(t.Program) is null || !ValidCode(t.PlanCode))
                errors.Add($"Tort recovery for '{t.Program}' / '{t.PlanCode}': program or plan code is not valid");
            if (!TexasTprReport.TryMonth(t.Month, out _)) errors.Add($"Tort recovery month '{t.Month}' must be yyyy-MM");
        }
        if (forWorkbook && string.IsNullOrWhiteSpace(Contractor)) errors.Add("contractor is required for the workbook");
        if (Contractor is { Length: > 200 } || (Contractor != null && Regex.IsMatch(Contractor, @"\p{Cc}")))
            errors.Add("contractor must be at most 200 characters with no control characters");
        if (McoCode != null && !Regex.IsMatch(McoCode, "^[A-Za-z0-9]{3}$")) errors.Add("mcoCode must be 3 letters or digits");
        return errors;
    }

    private static bool ValidCode(string? code) => code != null && Regex.IsMatch(code.Trim(), "^[A-Za-z0-9]{1,10}$");
}

public sealed record TexasTprPlan(string Program, string PlanCode);

public sealed record TexasTprAmount(string Program, string PlanCode, DateOnly Month, TexasTprLine Line, decimal Amount);

public sealed record TexasTprExclusion(string Reason, int Items, decimal Amount, IReadOnlyList<string> Examples);

public sealed record TexasTpr(
    int StateFiscalYear,
    int Quarter,
    DateOnly From,
    DateOnly Through,
    IReadOnlyList<TexasTprPlan> Plans,
    IReadOnlyList<TexasTprAmount> Amounts,
    IReadOnlyList<TexasTprExclusion> Excluded,
    IReadOnlyList<string> Notes);

/// <summary>
/// HHSC's Third Party Liability and Recoveries (TPL/TPR) Report (UMCM
/// 5.3.4.1, instructions 5.3.4.2): per program worksheet and plan code, each
/// month of the state fiscal year through the reporting quarter, the amounts
/// cost avoided, credited and recovered. Avoided and recovered amounts are
/// reported in the month of avoidance (adjudication) or recovery, in Texas
/// time. A claim's plan code is its member's rate cell in the month of
/// service, mapped by the request.
/// </summary>
public static class TexasTprReport
{
    public const string NoPlanCode = "Member has no expected member-month for the month of service, or its rate cell is not mapped to a plan code";
    public const string NotPriced = "Cost avoided claim has no priced Medicaid allowable (it was not priced, or was adjudicated before CHO recorded the amount)";

    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public static bool TryMonth(string? value, out DateOnly month) =>
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);

    public static DateOnly FiscalYearStart(int sfy) => new(sfy - 1, 9, 1);

    public static DateOnly QuarterEnd(int sfy, int quarter) => FiscalYearStart(sfy).AddMonths(quarter * 3 - 1);

    /// <summary>The Texas month an instant falls in.</summary>
    public static DateOnly TexasMonth(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Central);
        return new DateOnly(local.Year, local.Month, 1);
    }

    public static DateOnly TexasToday(DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Central));

    /// <summary>The window of adjudication and recovery instants to read: the SFY start through the quarter end, in Texas time, as UTC.</summary>
    public static (DateTime FromUtc, DateTime BeforeUtc) Window(int sfy, int quarter)
    {
        static DateTime Utc(DateOnly month) =>
            TimeZoneInfo.ConvertTimeToUtc(month.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Central);
        return (Utc(FiscalYearStart(sfy)), Utc(QuarterEnd(sfy, quarter).AddMonths(1)));
    }

    public static TexasTpr Build(
        int sfy, int quarter,
        IEnumerable<TplReportClaim> claims,
        IEnumerable<TplRecoveryCase> recoveryCases,
        IEnumerable<ExpectedRateCell> rateCells,
        IReadOnlyDictionary<string, (string Program, string PlanCode)> planCodes,
        IEnumerable<TexasTprTortRecovery> torts)
    {
        var from = FiscalYearStart(sfy);
        var through = QuarterEnd(sfy, quarter);
        var cellOf = new Dictionary<(string, DateOnly), string>();
        foreach (var r in rateCells) cellOf[(r.MemberId, r.Month)] = r.RateCell.Trim();

        var sums = new Dictionary<(string Program, string PlanCode, DateOnly Month, TexasTprLine Line), decimal>();
        var excluded = new Dictionary<string, (int Items, decimal Amount, SortedSet<string> Examples)>();
        var notes = new List<string>();

        void Exclude(string reason, decimal amount, string example)
        {
            (int Items, decimal Amount, SortedSet<string> Examples) e = excluded.TryGetValue(reason, out var found) ? found : (0, 0m, new SortedSet<string>(StringComparer.Ordinal));
            if (e.Examples.Count < 20) e.Examples.Add(example);
            excluded[reason] = (e.Items + 1, e.Amount + amount, e.Examples);
        }

        void Add(string memberId, DateTime serviceDate, DateOnly month, TexasTprLine line, decimal amount, string example)
        {
            if (month < from || month > through || amount == 0m) return;
            var serviceMonth = new DateOnly(serviceDate.Year, serviceDate.Month, 1);
            if (!cellOf.TryGetValue((memberId, serviceMonth), out var cell) || !planCodes.TryGetValue(cell, out var plan))
            {
                Exclude(NoPlanCode, amount, example);
                return;
            }
            var key = (plan.Program, plan.PlanCode, month, line);
            sums[key] = sums.GetValueOrDefault(key) + amount;
        }

        // A claim is counted once per line: its first version that qualifies,
        // so a resubmission or adjustment of the same claim is not counted again.
        foreach (var claim in claims.GroupBy(c => c.ClaimNumber))
        {
            var ordered = claim.OrderBy(c => c.AdjudicatedDate).ThenBy(c => c.VersionNumber).ToList();
            if (ordered.FirstOrDefault(c => c.Denied && c.TplDecision == "CostAvoided") is { } avoided)
            {
                if (avoided.CostAvoidedAllowedAmount is { } allowed)
                    Add(avoided.MemberId, avoided.ServiceDateFrom, TexasMonth(avoided.AdjudicatedDate), TexasTprLine.DeniedClaims, allowed, claim.Key);
                else if (TexasMonth(avoided.AdjudicatedDate) is var m && m >= from && m <= through)
                    Exclude(NotPriced, 0m, claim.Key);
            }
            if (ordered.FirstOrDefault(c => c.OtherPayerPaid != 0m) is { } credited)
                Add(credited.MemberId, credited.ServiceDateFrom, TexasMonth(credited.AdjudicatedDate),
                    TexasTprLine.OtherInsuranceCredits, credited.OtherPayerPaid, claim.Key);
        }

        var inferred = 0;
        foreach (var c in recoveryCases.Where(c => string.Equals(c.State, "TX", StringComparison.OrdinalIgnoreCase)))
            foreach (var e in c.Events.Where(e => e.Type == TplRecoveryEventType.PaymentReceived && e.Amount is > 0m))
            {
                var source = e.Source;
                if (source is null)
                {
                    source = c.Origin == TplRecoveryOrigin.PayAndChase ? TplRecoverySource.Carrier : TplRecoverySource.Provider;
                    if (TexasMonth(e.At) is var m && m >= from && m <= through) inferred++;
                }
                Add(c.MemberId, c.ServiceDateFrom, TexasMonth(e.At),
                    source == TplRecoverySource.Carrier ? TexasTprLine.OtherInsuranceRecoveries : TexasTprLine.ProviderRefundsAndRecoupments,
                    e.Amount!.Value, c.ClaimNumber ?? c.Id);
            }
        if (inferred > 0)
            notes.Add($"{inferred} recovery payment(s) had no source recorded: pay-and-chase cases were counted as insurance recoveries, retroactive ones as provider refunds");

        foreach (var t in torts)
        {
            TryMonth(t.Month, out var month);
            if (month < from || month > through)
            {
                notes.Add($"Tort recovery for {t.PlanCode} in {t.Month} is outside SFY {sfy} through Q{quarter} and was left out");
                continue;
            }
            var key = (TexasTprPrograms.Resolve(t.Program)!, t.PlanCode.Trim(), month, TexasTprLine.TortRecoveries);
            sums[key] = sums.GetValueOrDefault(key) + t.Amount;
        }

        if (excluded.Count > 0)
            notes.Add("Some amounts were left out (see excluded); check the rate cell mapping and the expected member-months loaded");

        var plans = planCodes.Values.Select(p => new TexasTprPlan(p.Program, p.PlanCode))
            .Concat(sums.Keys.Select(k => new TexasTprPlan(k.Program, k.PlanCode)))
            .Distinct()
            .OrderBy(p => TexasTprPrograms.All.ToList().IndexOf(p.Program)).ThenBy(p => p.PlanCode, StringComparer.Ordinal)
            .ToList();

        return new TexasTpr(sfy, quarter, from, through, plans,
            sums.Where(s => s.Value != 0m)
                .OrderBy(s => s.Key.Program).ThenBy(s => s.Key.PlanCode).ThenBy(s => s.Key.Month).ThenBy(s => s.Key.Line)
                .Select(s => new TexasTprAmount(s.Key.Program, s.Key.PlanCode, s.Key.Month, s.Key.Line, s.Value)).ToList(),
            excluded.Select(e => new TexasTprExclusion(e.Key, e.Value.Items, e.Value.Amount, e.Value.Examples.ToList())).ToList(),
            notes);
    }

    // ---- The workbook -----------------------------------------------------

    /// <summary>Where one program sheet's months, lines and plan code columns are.</summary>
    public sealed record SheetLayout(
        string Sheet,
        IReadOnlyDictionary<int, int> MonthBlockRows,
        IReadOnlyList<string> PlanCodeColumns,
        int PlanCodeRow);

    private static readonly (TexasTprLine Line, int Offset, string Label)[] LineRows =
    [
        (TexasTprLine.DeniedClaims, 2, "Denied Claims"),
        (TexasTprLine.OtherInsuranceCredits, 4, "Other Insurance Credits"),
        (TexasTprLine.OtherInsuranceRecoveries, 6, "Other Insurance Recoveries"),
        (TexasTprLine.ProviderRefundsAndRecoupments, 7, "Provider Refunds & Recoupments"),
        (TexasTprLine.TortRecoveries, 8, "Tort Recoveries"),
    ];

    private static readonly (string Cell, string Label)[] HeaderCells =
        [("A2", "Contractor:"), ("A3", "SFY:"), ("A4", "Quarter:"), ("A5", "Date Submitted:")];

    /// <summary>
    /// Reads one program sheet from its own labels: the month blocks (column
    /// B names the month, column A the quarter), the five line labels under
    /// each, and the "Plan Code" columns.
    /// </summary>
    public static (SheetLayout? Layout, List<string> Problems) ReadLayout(XlsxTemplate workbook, string program)
    {
        if (!workbook.SheetNames.Contains(program)) return (null, [$"The workbook has no '{program}' sheet"]);
        var problems = new List<string>();
        foreach (var (cell, label) in HeaderCells)
            if (!Normalize(workbook.Text(program, cell)).StartsWith(label, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{program}!{cell} should start \"{label}\"");

        var columnA = workbook.ColumnTexts(program, "A");
        var blocks = new Dictionary<int, int>();
        foreach (var (row, text) in workbook.ColumnTexts(program, "B"))
        {
            if (!DateTime.TryParseExact(Normalize(text), "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!Normalize(columnA.GetValueOrDefault(row)).StartsWith('Q')) continue;
            foreach (var (_, offset, label) in LineRows)
                if (!Normalize(workbook.Text(program, $"B{row + offset}")).Equals(label, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{program}!B{row + offset} should be \"{label}\"");
            blocks[d.Month] = row;
        }
        if (blocks.Count != 12) problems.Add($"{program} should have a block for each of the 12 months; found {blocks.Count}");
        if (problems.Count > 0) return (null, problems);

        var september = blocks[9];
        var columns = workbook.RowTexts(program, september)
            .Where(c => Normalize(c.Text).Equals("Plan Code", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Column).ToList();
        if (columns.Count == 0) return (null, [$"{program} row {september} has no 'Plan Code' columns"]);
        return (new SheetLayout(program, blocks, columns, september + 1), problems);
    }

    /// <summary>Reads every program sheet the report has plan codes for; problems when one is missing or has too few columns.</summary>
    public static (Dictionary<string, SheetLayout> Layouts, List<string> Problems) ReadLayouts(XlsxTemplate workbook, TexasTpr report)
    {
        var layouts = new Dictionary<string, SheetLayout>();
        var problems = new List<string>();
        foreach (var program in report.Plans.Select(p => p.Program).Distinct())
        {
            var (layout, p) = ReadLayout(workbook, program);
            problems.AddRange(p);
            if (layout is null) continue;
            var count = report.Plans.Count(x => x.Program == program);
            if (count > layout.PlanCodeColumns.Count)
                problems.Add($"{program} has {count} plan codes but the sheet has {layout.PlanCodeColumns.Count} plan code columns");
            layouts[program] = layout;
        }
        return (layouts, problems);
    }

    /// <summary>
    /// Writes each program sheet's header, plan codes and the five lines for
    /// every month through the quarter (zero where there is nothing, as
    /// 5.3.4.2 asks), and the Data Certification fields CHO knows. The
    /// header goes in the label cell itself ("Contractor: …"): HHSC merges
    /// and unlocks only that cell. Totals stay HHSC's formulas.
    /// </summary>
    public static byte[] Fill(XlsxTemplate workbook, IReadOnlyDictionary<string, SheetLayout> layouts, TexasTpr report, string contractor, DateOnly submissionDate)
    {
        var amounts = report.Amounts.ToDictionary(a => (a.Program, a.PlanCode, a.Month, a.Line), a => a.Amount);
        var submitted = submissionDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        foreach (var (program, layout) in layouts)
        {
            var s = layout.Sheet;
            workbook.SetText(s, "A2", $"Contractor: {contractor.Trim()}");
            workbook.SetText(s, "A3", $"SFY: {report.StateFiscalYear}");
            workbook.SetText(s, "A4", $"Quarter: Q{report.Quarter}");
            workbook.SetText(s, "A5", $"Date Submitted: {submitted}");

            var plans = report.Plans.Where(p => p.Program == program).ToList();
            for (var i = 0; i < plans.Count; i++)
            {
                var column = layout.PlanCodeColumns[i];
                workbook.SetText(s, column + layout.PlanCodeRow.ToString(CultureInfo.InvariantCulture), plans[i].PlanCode);
                for (var month = report.From; month <= report.Through; month = month.AddMonths(1))
                {
                    var block = layout.MonthBlockRows[month.Month];
                    foreach (var (line, offset, _) in LineRows)
                        workbook.SetNumber(s, column + (block + offset).ToString(CultureInfo.InvariantCulture),
                            amounts.GetValueOrDefault((program, plans[i].PlanCode, month, line)));
                }
            }
            workbook.ClearCachedValues(s);
        }
        if (workbook.SheetNames.Contains("OIG TPR Use")) workbook.ClearCachedValues("OIG TPR Use");

        const string cert = "Certification";
        if (workbook.SheetNames.Contains(cert)
            && Normalize(workbook.Text(cert, "B3")).Equals("MCO Name", StringComparison.OrdinalIgnoreCase)
            && Normalize(workbook.Text(cert, "B5")).Equals("MCO Plan Code Numbers", StringComparison.OrdinalIgnoreCase)
            && Normalize(workbook.Text(cert, "B7")).Equals("Document Name", StringComparison.OrdinalIgnoreCase)
            && Normalize(workbook.Text(cert, "B9")).Equals("Date of Submission", StringComparison.OrdinalIgnoreCase))
        {
            workbook.SetText(cert, "B2", contractor.Trim());
            workbook.SetText(cert, "B4", string.Join(", ", report.Plans.Where(p => layouts.ContainsKey(p.Program)).Select(p => p.PlanCode).Distinct()));
            workbook.SetText(cert, "B6", $"TPL/TPR Report for Quarter {report.Quarter}, SFY {report.StateFiscalYear}");
            workbook.SetText(cert, "B8", submitted);
        }
        return workbook.Save();
    }

    /// <summary>HHSC's file name: MCO code, TPR, Q, the quarter (01-04), and the MMYY of the quarter's last month.</summary>
    public static string FileName(TexasTpr report, string? mcoCode) =>
        mcoCode is null
            ? $"TPR-SFY{report.StateFiscalYear}-Q{report.Quarter}.xlsx"
            : $"{mcoCode.ToUpperInvariant()}TPRQ{report.Quarter:00}{report.Through:MMyy}.xlsx";

    private static string Normalize(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
