using System.Globalization;
using AppealsService.Models;
using CloudHealthOffice.Infrastructure.Reporting;
using static AppealsService.Models.AppealInsightSets;

namespace AppealsService.Reports;

/// <summary>
/// Appeals and grievances standard reports (portal-reporting.md §4.7): the
/// case log a Medicaid MCO keeps under 42 CFR 438.416, timeliness by month
/// and level against each case's own (possibly extended) deadline, and the
/// open cases by deadline. Received, decided, on time and overdue mean what
/// they mean in Appeals &amp; Grievances Insights (<see cref="AppealInsightSets"/>).
/// </summary>
public static class AgReportCatalog
{
    /// <summary>UM / A&amp;G managers (<c>reports:um</c>) or compliance (<c>reports:compliance</c>).</summary>
    public const string Permission = "reports:um,reports:compliance";

    public const string CaseLog = "ag-case-log";
    public const string TimelinessByMonth = "ag-timeliness-by-month";
    public const string OpenByDeadline = "ag-open-by-deadline";

    private static readonly ReportParameter From = new("from", "date", false, "Start of the receipt period, inclusive (default: 30 days before to).");
    private static readonly ReportParameter To = new("to", "date", false, "End of the receipt period, exclusive (default: now).");
    private static readonly ReportParameter Lob = new("lineOfBusiness", "LineOfBusiness", false, "Only this line of business.");
    private static readonly ReportParameter Kind = new("kind", "string", false, "Appeal or Grievance.");

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(CaseLog, "Appeal and grievance log",
            "Every appeal and grievance received in [from, to): who, what level, received, due, resolved, resolution and whether it was on time.",
            Permission,
            [From, To, Lob, Kind],
            ["AppealId", "AppealNumber", "Kind", "Level", "Expedited", "MemberId", "LineOfBusiness", "ReceivedAt", "DueAt", "Extended",
             "Status", "ResolvedAt", "Resolution", "OnTime", "ClaimNumber", "DenialReasonCode", "ProviderNpi"],
            "42 CFR 438.416 (Medicaid managed care grievance and appeal records)"),
        new(TimelinessByMonth, "Appeal and grievance timeliness by month",
            "For each receipt month and level: received, resolved on time, resolved late, closed without a decision, open past due or not yet due, and overturns.",
            Permission,
            [new("from", "date", false, "Start of the receipt period, inclusive (default: 12 months before to)."), To, Lob],
            ["ReceiptMonth", "Level", "Received", "ResolvedOnTime", "ResolvedLate", "ClosedWithoutDecision", "OpenPastDue", "OpenNotDue",
             "PercentOnTime", "Overturned", "PercentOverturned"],
            "42 CFR 438.408 (resolution timeframes)"),
        new(OpenByDeadline, "Open appeals and grievances by deadline",
            "Every open case, soonest deadline first, with the days left (negative: overdue).",
            Permission,
            [Lob, Kind],
            ["AppealId", "AppealNumber", "Kind", "Level", "Expedited", "Status", "ReceivedAt", "DueAt", "DaysLeft", "Extended",
             "AssignedReviewerId", "LineOfBusiness"],
            null),
    ];

    public static ReportDefinition Find(string id) => All.Single(d => d.Id == id);
}

/// <summary>
/// One case in the 438.416 log. The enrollee is identified by member id
/// (the regulation requires the record to name the enrollee); the reason
/// text is not included.
/// </summary>
public sealed record AgCaseRow(
    string AppealId,
    string AppealNumber,
    string Kind,
    string Level,
    bool Expedited,
    string MemberId,
    LineOfBusiness LineOfBusiness,
    DateTime ReceivedAt,
    DateTime? DueAt,
    bool Extended,
    AppealStatus Status,
    DateTime? ResolvedAt,
    string? Resolution,
    bool? OnTime,
    string? ClaimNumber,
    string? DenialReasonCode,
    string ProviderNpi);

public sealed record AgMonthRow(
    string ReceiptMonth,
    string Level,
    int Received,
    int ResolvedOnTime,
    int ResolvedLate,
    int ClosedWithoutDecision,
    int OpenPastDue,
    int OpenNotDue,
    decimal? PercentOnTime,
    int Overturned,
    decimal? PercentOverturned);

public sealed record AgOpenRow(
    string AppealId,
    string AppealNumber,
    string Kind,
    string Level,
    bool Expedited,
    AppealStatus Status,
    DateTime ReceivedAt,
    DateTime? DueAt,
    double? DaysLeft,
    bool Extended,
    string? AssignedReviewerId,
    LineOfBusiness LineOfBusiness);

public static class AgReportBuilder
{
    public const string AppealKind = "Appeal";
    public const string GrievanceKind = "Grievance";

    private static readonly string[] Levels =
        [nameof(AppealLevel.FirstLevel), nameof(AppealLevel.SecondLevel), nameof(AppealLevel.ExternalReview), GrievanceLevel];

    public static string Kind(Appeal a) => IsGrievance(a) ? GrievanceKind : AppealKind;

    private static bool KindMatches(Appeal a, string? kind) =>
        kind is null || string.Equals(Kind(a), kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On time: decided by the deadline. Late: decided after it (or with no
    /// deadline), or still open past it. Null: open and not yet due, or closed
    /// without a decision (withdrawn, expired, administrative).
    /// </summary>
    public static bool? OnTime(Appeal a, DateTime asOf)
    {
        if (IsDecided(a)) return IsOnTime(a);
        if (IsOpen(a)) return IsOverdue(a, asOf) ? false : null;
        return null;
    }

    /// <summary>Cases received in [from, to), in the line of business and kind, oldest first.</summary>
    public static IEnumerable<Appeal> Received(IEnumerable<Appeal> activity, DateTime from, DateTime to, LineOfBusiness? lob, string? kind) =>
        activity
            .Where(a => a.Status != AppealStatus.Draft)
            .Where(a => ReceivedAt(a) is var r && r >= from && r < to)
            .Where(a => lob is null || a.LineOfBusiness == lob)
            .Where(a => KindMatches(a, kind))
            .OrderBy(ReceivedAt).ThenBy(a => a.Id, StringComparer.Ordinal);

    public static AgCaseRow Case(Appeal a, DateTime asOf) => new(
        a.Id, a.AppealNumber, Kind(a), LevelKey(a), a.IsUrgent, a.MemberId, a.LineOfBusiness,
        ReceivedAt(a), a.TargetResponseDate, a.DeadlineExtension is not null, a.Status,
        a.Status == AppealStatus.Closed ? DecidedAt(a) : null,
        a.Status == AppealStatus.Closed ? a.ClosureReasonCode?.ToString() : null,
        OnTime(a, asOf),
        IsGrievance(a) || string.IsNullOrWhiteSpace(a.ClaimNumber) ? null : a.ClaimNumber,
        IsGrievance(a) ? null : a.DenialReasonCode,
        a.ProviderNPI);

    public static IEnumerable<Appeal> Open(IEnumerable<Appeal> open, LineOfBusiness? lob, string? kind) =>
        open
            .Where(IsOpen)
            .Where(a => lob is null || a.LineOfBusiness == lob)
            .Where(a => KindMatches(a, kind))
            // No deadline sorts last: it can't be prioritized by date.
            .OrderBy(a => a.TargetResponseDate ?? DateTime.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal);

    public static AgOpenRow OpenCase(Appeal a, DateTime asOf) => new(
        a.Id, a.AppealNumber, Kind(a), LevelKey(a), a.IsUrgent, a.Status, ReceivedAt(a), a.TargetResponseDate,
        a.TargetResponseDate is { } due ? Math.Round((due - asOf).TotalDays, 1) : null,
        a.DeadlineExtension is not null, a.AssignedReviewerId, a.LineOfBusiness);

    /// <summary>One row per receipt month and level in [from, to); empty months show zeros.</summary>
    public static IReadOnlyList<AgMonthRow> ByMonth(IEnumerable<Appeal> received, DateTime from, DateTime to, DateTime asOf)
    {
        var counts = new Dictionary<(DateTime Month, string Level), int[]>();
        var months = new List<DateTime>();
        for (var m = StandardReports.MonthOf(from); m < to; m = m.AddMonths(1))
        {
            months.Add(m);
            foreach (var level in Levels) counts[(m, level)] = new int[8];
        }

        foreach (var a in received)
        {
            if (!counts.TryGetValue((StandardReports.MonthOf(ReceivedAt(a)), LevelKey(a)), out var c)) continue;
            c[0]++;
            if (IsDecided(a))
            {
                if (IsOnTime(a)) c[1]++;
                else c[2]++;
                c[6]++;
                if (!IsGrievance(a) && IsOverturned(a)) c[7]++;
            }
            else if (IsOpen(a))
            {
                if (IsOverdue(a, asOf)) c[4]++;
                else c[5]++;
            }
            else c[3]++;
        }

        return months.SelectMany(m => Levels.Select(level =>
        {
            var c = counts[(m, level)];
            var decided = c[6];
            var grievance = level == GrievanceLevel;
            return new AgMonthRow(
                m.ToString("yyyy-MM", CultureInfo.InvariantCulture), level,
                c[0], c[1], c[2], c[3], c[4], c[5],
                decided == 0 ? null : Math.Round((decimal)c[1] / decided, 4),
                c[7],
                grievance || decided == 0 ? null : Math.Round((decimal)c[7] / decided, 4));
        })).ToList();
    }
}
