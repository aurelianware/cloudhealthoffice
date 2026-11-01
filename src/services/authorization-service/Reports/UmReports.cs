using System.Globalization;
using AuthorizationService.Models;
using AuthorizationService.Services;
using AuthorizationService.Services.Cms0057;
using AuthorizationService.Services.Fhir;
using AuthorizationService.Services.Retention;
using CloudHealthOffice.Infrastructure.Reporting;

namespace AuthorizationService.Reports;

/// <summary>
/// Utilization management standard reports (portal-reporting.md §4.7):
/// decision timeliness row by row and by month, and the open requests by
/// deadline. Requests are the non-drug ones the CMS-0057-F metrics count;
/// timeframes are the request's own deadline (72 hours expedited, 7 calendar
/// days standard, or a lawful extension), the same clock as the SLA watchdog.
/// Rows carry no member id or name.
/// </summary>
public static class UmReportCatalog
{
    /// <summary>UM managers (<c>reports:um</c>) or compliance (<c>reports:compliance</c>).</summary>
    public const string Permission = "reports:um,reports:compliance";

    public const string DecisionTimeliness = "um-decision-timeliness";
    public const string TimelinessByMonth = "um-timeliness-by-month";
    public const string PendingByDeadline = "um-pending-by-deadline";

    private const string Regulation =
        "42 CFR 438.210(d) (Medicaid managed care), 42 CFR 422.568/422.572 (MA), 45 CFR 156.223; 72 hours expedited, 7 calendar days standard";

    private static readonly ReportParameter From = new("from", "date", false, "Start of the receipt period, inclusive (default: 30 days before to).");
    private static readonly ReportParameter To = new("to", "date", false, "End of the receipt period, exclusive (default: now).");
    private static readonly ReportParameter Lob = new("lineOfBusiness", "LineOfBusiness", false, "Only this line of business.");
    private static readonly ReportParameter RequestType = new("requestType", "string", false, "Standard or Expedited.");

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(DecisionTimeliness, "Prior authorization decision timeliness",
            "Every request received in [from, to): received, due, decided, outcome and whether the decision met its timeframe.",
            Permission,
            [From, To, Lob, RequestType, new("withinTimeframe", "bool", false, "true: only on-time decisions; false: only late or overdue ones.")],
            ["AuthorizationId", "AuthorizationNumber", "RequestType", "ReceivedAt", "DueAt", "DecidedAt", "Decision", "HoursToDecision",
             "WithinTimeframe", "Extended", "Automated", "LineOfBusiness", "ServiceTypeCode", "RequestingProviderNpi"],
            Regulation),
        new(TimelinessByMonth, "Prior authorization timeliness by month",
            "For each receipt month and request type: received, decided on time, decided late, and still open past or within the deadline.",
            Permission,
            [new("from", "date", false, "Start of the receipt period, inclusive (default: 12 months before to)."), To, Lob],
            ["ReceiptMonth", "RequestType", "Received", "DecidedOnTime", "DecidedLate", "OpenPastDue", "OpenNotDue",
             "DecidedWithoutTiming", "PercentOnTime"],
            Regulation),
        new(PendingByDeadline, "Open requests by deadline",
            "Every request not yet decided, soonest deadline first, with the hours left (negative: overdue).",
            Permission,
            [Lob, RequestType],
            ["AuthorizationId", "AuthorizationNumber", "RequestType", "Status", "ReceivedAt", "DueAt", "HoursLeft", "SlaLevel",
             "Extended", "LineOfBusiness", "ServiceTypeCode", "RequestingProviderNpi"],
            Regulation),
    ];

    public static ReportDefinition Find(string id) => All.Single(d => d.Id == id);
}

public sealed record UmDecisionRow(
    string AuthorizationId,
    string AuthorizationNumber,
    string RequestType,
    DateTime ReceivedAt,
    DateTime DueAt,
    DateTime? DecidedAt,
    string Decision,
    double? HoursToDecision,
    bool? WithinTimeframe,
    bool Extended,
    bool Automated,
    LineOfBusiness LineOfBusiness,
    string? ServiceTypeCode,
    string RequestingProviderNpi);

public sealed record UmMonthRow(
    string ReceiptMonth,
    string RequestType,
    int Received,
    int DecidedOnTime,
    int DecidedLate,
    int OpenPastDue,
    int OpenNotDue,
    int DecidedWithoutTiming,
    decimal? PercentOnTime);

public sealed record UmPendingRow(
    string AuthorizationId,
    string AuthorizationNumber,
    string RequestType,
    AuthorizationStatus Status,
    DateTime ReceivedAt,
    DateTime DueAt,
    double HoursLeft,
    SlaEscalationLevel SlaLevel,
    bool Extended,
    LineOfBusiness LineOfBusiness,
    string? ServiceTypeCode,
    string RequestingProviderNpi);

public static class UmReportBuilder
{
    public const string Standard = "Standard";
    public const string Expedited = "Expedited";

    public static string RequestType(Authorization a) => Cms0057Reporting.IsExpedited(a) ? Expedited : Standard;

    /// <summary>The requests the report counts: non-drug, received in [from, to), in the line of business and request type.</summary>
    public static IEnumerable<Authorization> Received(
        IEnumerable<Authorization> authorizations, DateTime from, DateTime to, LineOfBusiness? lob, string? requestType) =>
        authorizations
            .Where(PriorAuthorizationApiScope.IsInScope)
            .Where(a => AuthorizationDeadlinePolicy.Receipt(a) is var r && r >= from && r < to)
            .Where(a => lob is null || a.LineOfBusiness == lob)
            .Where(a => requestType is null || string.Equals(RequestType(a), requestType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(AuthorizationDeadlinePolicy.Receipt).ThenBy(a => a.Id, StringComparer.Ordinal);

    /// <summary>
    /// Met the timeframe: decided by the deadline. Late: decided after it, or
    /// undecided and past it. Null: undecided and not yet due, or decided
    /// with no usable decision time.
    /// </summary>
    public static bool? WithinTimeframe(Authorization a, DateTime asOf)
    {
        if (Cms0057Reporting.IsDecided(a))
            return Cms0057Reporting.DecisionHours(a) is { } hours ? hours <= Cms0057Reporting.DeadlineHours(a) : null;
        return asOf > AuthorizationDeadlinePolicy.Deadline(a) ? false : null;
    }

    public static UmDecisionRow Decision(Authorization a, DateTime asOf)
    {
        var decided = Cms0057Reporting.IsDecided(a);
        var hours = Cms0057Reporting.DecisionHours(a);
        return new UmDecisionRow(
            a.Id, a.AuthorizationNumber, RequestType(a),
            AuthorizationDeadlinePolicy.Receipt(a), AuthorizationDeadlinePolicy.Deadline(a),
            decided ? a.ReviewedDate : null,
            (decided ? Cms0057Reporting.DecisionStatus(a) : a.Status).ToString(),
            hours is { } h ? Math.Round(h, 1) : null,
            WithinTimeframe(a, asOf),
            Cms0057Reporting.WasExtended(a),
            decided && Cms0057Reporting.IsAutomated(a),
            a.LineOfBusiness, a.ServiceTypeCode, a.RequestingProviderNPI);
    }

    public static UmPendingRow Pending(Authorization a, DateTime asOf)
    {
        var due = AuthorizationDeadlinePolicy.Deadline(a);
        return new UmPendingRow(
            a.Id, a.AuthorizationNumber, RequestType(a), a.Status,
            AuthorizationDeadlinePolicy.Receipt(a), due,
            Math.Round((due - asOf).TotalHours, 1),
            SlaWatchdogService.ComputeSlaStatus(a, asOf).EscalationLevel,
            Cms0057Reporting.WasExtended(a),
            a.LineOfBusiness, a.ServiceTypeCode, a.RequestingProviderNPI);
    }

    /// <summary>The open requests, soonest deadline first.</summary>
    public static IEnumerable<Authorization> Open(IEnumerable<Authorization> open, LineOfBusiness? lob, string? requestType) =>
        open
            .Where(a => a.Status.IsOpen())
            .Where(PriorAuthorizationApiScope.IsInScope)
            .Where(a => lob is null || a.LineOfBusiness == lob)
            .Where(a => requestType is null || string.Equals(RequestType(a), requestType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(AuthorizationDeadlinePolicy.Deadline).ThenBy(a => a.Id, StringComparer.Ordinal);

    /// <summary>One row per receipt month and request type in [from, to); empty months show zeros.</summary>
    public static IReadOnlyList<UmMonthRow> ByMonth(IEnumerable<Authorization> received, DateTime from, DateTime to, DateTime asOf)
    {
        var counts = new SortedDictionary<(DateTime Month, string Type), int[]>();
        for (var m = StandardReports.MonthOf(from); m < to; m = m.AddMonths(1))
        {
            counts[(m, Expedited)] = new int[6];
            counts[(m, Standard)] = new int[6];
        }

        foreach (var a in received)
        {
            var key = (StandardReports.MonthOf(AuthorizationDeadlinePolicy.Receipt(a)), RequestType(a));
            if (!counts.TryGetValue(key, out var c)) continue;
            c[0]++;
            var within = WithinTimeframe(a, asOf);
            if (Cms0057Reporting.IsDecided(a))
            {
                if (within is true) c[1]++;
                else if (within is false) c[2]++;
                else c[5]++;
            }
            else if (within is false) c[3]++;
            else c[4]++;
        }

        return counts
            .OrderBy(kv => kv.Key.Month).ThenBy(kv => kv.Key.Type == Standard ? 0 : 1)
            .Select(kv =>
            {
                var c = kv.Value;
                var timed = c[1] + c[2];
                return new UmMonthRow(
                    kv.Key.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture), kv.Key.Type,
                    c[0], c[1], c[2], c[3], c[4], c[5],
                    timed == 0 ? null : Math.Round((decimal)c[1] / timed, 4));
            })
            .ToList();
    }
}
