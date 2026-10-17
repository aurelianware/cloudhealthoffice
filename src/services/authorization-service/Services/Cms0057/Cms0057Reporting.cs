using AuthorizationService.Models;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Services.Cms0057;

/// <summary>
/// The CMS-0057-F prior-authorization metrics (45 CFR 156.223 and the parallel
/// MA/Medicaid/CHIP provisions) computed from the tenant's authorizations, plus
/// the recent-decision list the portal's CMS-0057-F console shows.
///
/// Request type follows the SLA watchdog: level of service <c>U</c> (urgent,
/// 278 UM04) is an expedited request with a 72-hour decision timeframe;
/// everything else is a standard request with 7 calendar days (168 hours).
/// Elapsed time is measured from submission to decision, as the rule's
/// public-reporting metric defines it (not the RFAI-resumed SLA clock).
/// </summary>
public static class Cms0057Reporting
{
    public const double ExpeditedTimeframeHours = 72;
    public const double StandardTimeframeHours = 168;

    /// <summary>The decided outcomes. Modified (approved with changes) counts as approved.</summary>
    public static bool IsDecided(AuthorizationStatus status) =>
        status is AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied;

    public static bool IsApproved(AuthorizationStatus status) =>
        status is AuthorizationStatus.Approved or AuthorizationStatus.Modified;

    public static bool IsExpedited(Authorization auth) =>
        string.Equals(auth.LevelOfService, "U", StringComparison.OrdinalIgnoreCase);

    public static double TimeframeHours(Authorization auth) =>
        IsExpedited(auth) ? ExpeditedTimeframeHours : StandardTimeframeHours;

    public static double? DecisionHours(Authorization auth) =>
        IsDecided(auth.Status) && auth.ReviewedDate is { } reviewed
            ? Math.Max(0, (reviewed - auth.SubmittedDate).TotalHours)
            : null;

    /// <summary>
    /// The history entry that recorded the decision (the latest change to the
    /// current decided status), or null when the record carries no history.
    /// </summary>
    public static AuthorizationStatusChange? DecisionEntry(Authorization auth) =>
        auth.StatusHistory
            .Where(h => h.Status == auth.Status)
            .OrderByDescending(h => h.ChangedAt)
            .FirstOrDefault();

    /// <summary>True when an automated rule, not a reviewer, made the decision.</summary>
    public static bool IsAutomated(Authorization auth) =>
        DecisionEntry(auth)?.Source?.StartsWith("automated", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The requests a period's metrics count: submitted in [from, to], in the line of business.</summary>
    public static IEnumerable<Authorization> InPeriod(
        IEnumerable<Authorization> authorizations, DateTime from, DateTime to, LineOfBusiness? lineOfBusiness) =>
        authorizations
            .Where(a => a.SubmittedDate >= from && a.SubmittedDate <= to)
            .Where(a => lineOfBusiness is null || a.LineOfBusiness == lineOfBusiness);

    /// <summary>Decided within its request type's timeframe (72h expedited, 168h standard).</summary>
    public static bool DecidedWithinTimeframe(Authorization auth) =>
        DecisionHours(auth) is { } hours && hours <= TimeframeHours(auth);

    public static Cms0057Metrics ComputeMetrics(
        IEnumerable<Authorization> authorizations, DateTime from, DateTime to, LineOfBusiness? lineOfBusiness)
    {
        var inPeriod = InPeriod(authorizations, from, to, lineOfBusiness).ToList();

        var decided = inPeriod.Where(a => IsDecided(a.Status)).ToList();
        return new Cms0057Metrics
        {
            From = from,
            To = to,
            LineOfBusiness = lineOfBusiness,
            TotalRequests = inPeriod.Count,
            OpenRequests = inPeriod.Count(a => a.Status.IsOpen()),
            Standard = Group(inPeriod.Where(a => !IsExpedited(a)).ToList(), StandardTimeframeHours),
            Expedited = Group(inPeriod.Where(IsExpedited).ToList(), ExpeditedTimeframeHours),
            AutomatedDecisions = decided.Count(IsAutomated),
            DecidedRequests = decided.Count,
            NotYetTracked =
            [
                new("Approved after appeal",
                    "Appeal outcomes are not yet linked back to the original authorization."),
                new("Extended review timeframe, then approved",
                    "Authorizations do not yet record a timeframe extension."),
            ],
        };
    }

    private static Cms0057RequestTypeMetrics Group(IReadOnlyList<Authorization> requests, double timeframeHours)
    {
        var decided = requests.Where(a => IsDecided(a.Status)).ToList();
        var hours = decided.Select(DecisionHours).OfType<double>().OrderBy(h => h).ToList();
        return new Cms0057RequestTypeMetrics
        {
            TimeframeHours = timeframeHours,
            Requests = requests.Count,
            Decided = decided.Count,
            Approved = decided.Count(a => IsApproved(a.Status)),
            Denied = decided.Count(a => a.Status == AuthorizationStatus.Denied),
            ApprovedPercent = Percent(decided.Count(a => IsApproved(a.Status)), decided.Count),
            DeniedPercent = Percent(decided.Count(a => a.Status == AuthorizationStatus.Denied), decided.Count),
            AverageDecisionHours = hours.Count == 0 ? null : Math.Round(hours.Average(), 1),
            MedianDecisionHours = hours.Count == 0 ? null : Math.Round(Median(hours), 1),
            DecidedWithinTimeframe = hours.Count(h => h <= timeframeHours),
            DecidedWithinTimeframePercent = Percent(hours.Count(h => h <= timeframeHours), hours.Count),
        };
    }

    public static IReadOnlyList<Cms0057Decision> RecentDecisions(
        IEnumerable<Authorization> authorizations, DateTime since, int limit)
    {
        return authorizations
            .Where(a => IsDecided(a.Status) && a.ReviewedDate >= since)
            .OrderByDescending(a => a.ReviewedDate)
            .Take(limit)
            .Select(a =>
            {
                var entry = DecisionEntry(a);
                var hours = DecisionHours(a);
                var timeframe = TimeframeHours(a);
                return new Cms0057Decision
                {
                    Id = a.Id,
                    AuthorizationNumber = a.AuthorizationNumber,
                    MemberId = a.MemberId,
                    RequestingProviderName = a.RequestingProviderName,
                    RequestType = IsExpedited(a) ? "Expedited" : "Standard",
                    Status = a.Status,
                    SubmittedDate = a.SubmittedDate,
                    DecidedDate = a.ReviewedDate!.Value,
                    DecisionHours = hours is null ? null : Math.Round(hours.Value, 1),
                    TimeframeHours = timeframe,
                    WithinTimeframe = hours is not null && hours.Value <= timeframe,
                    Automated = IsAutomated(a),
                    DecidedBy = IsAutomated(a)
                        ? entry?.Source
                        : a.ReviewerName ?? entry?.ChangedBy,
                    Reason = a.Status == AuthorizationStatus.Denied
                        ? a.DenialReason ?? entry?.Reason
                        : entry?.Reason ?? a.ReviewDecision,
                    DenialReasonCode = a.DenialReasonCode,
                };
            })
            .ToList();
    }

    private static double? Percent(int part, int whole) =>
        whole == 0 ? null : Math.Round(100.0 * part / whole, 1);

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

public sealed class Cms0057Metrics
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public LineOfBusiness? LineOfBusiness { get; set; }
    public int TotalRequests { get; set; }
    public int OpenRequests { get; set; }
    public int DecidedRequests { get; set; }
    public int AutomatedDecisions { get; set; }
    public Cms0057RequestTypeMetrics Standard { get; set; } = new();
    public Cms0057RequestTypeMetrics Expedited { get; set; } = new();
    /// <summary>Required metrics the data can't support yet, and why.</summary>
    public List<Cms0057MetricGap> NotYetTracked { get; set; } = new();
}

public sealed class Cms0057RequestTypeMetrics
{
    public double TimeframeHours { get; set; }
    public int Requests { get; set; }
    public int Decided { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    /// <summary>Of decided requests; null when none were decided.</summary>
    public double? ApprovedPercent { get; set; }
    public double? DeniedPercent { get; set; }
    public double? AverageDecisionHours { get; set; }
    public double? MedianDecisionHours { get; set; }
    public int DecidedWithinTimeframe { get; set; }
    public double? DecidedWithinTimeframePercent { get; set; }
}

public sealed record Cms0057MetricGap(string Metric, string Reason);

public sealed class Cms0057Decision
{
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? RequestingProviderName { get; set; }
    public string RequestType { get; set; } = "Standard";
    public AuthorizationStatus Status { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime DecidedDate { get; set; }
    public double? DecisionHours { get; set; }
    public double TimeframeHours { get; set; }
    public bool WithinTimeframe { get; set; }
    public bool Automated { get; set; }
    public string? DecidedBy { get; set; }
    public string? Reason { get; set; }
    public string? DenialReasonCode { get; set; }
}
