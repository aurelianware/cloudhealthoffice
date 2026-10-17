using AuthorizationService.Services;
using AuthorizationService.Services.Cms0057;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Models;

/// <summary>Authorization Insights (GET /api/authorizations/insights): aggregates only.</summary>
public sealed class AuthorizationInsights
{
    public DateTime AsOf { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public LineOfBusiness? LineOfBusiness { get; init; }

    /// <summary>
    /// Requests submitted in the period: volume, approval and denial rates and
    /// decision timeliness (standard vs expedited). The CMS-0057 public
    /// reporting figures, computed by the same code as the regulator report.
    /// </summary>
    public Cms0057Metrics Reporting { get; init; } = new();

    /// <summary>The review queue now: every open request, whenever submitted.</summary>
    public AuthorizationQueueInsight Queue { get; init; } = new();

    /// <summary>Requests submitted in the period and denied, by reason code (top 10).</summary>
    public List<AuthorizationDenialReasonInsight> DenialReasons { get; init; } = new();

    /// <summary>Approved authorizations whose service period ended in the period with no claim yet.</summary>
    public AuthorizationUnusedInsight Unused { get; init; } = new();
}

public sealed class AuthorizationUnusedInsight
{
    /// <summary>Approved authorizations whose approved service period ended in the period.</summary>
    public int Ended { get; init; }

    /// <summary>Of those, the ones no claim carries yet; null when claim usage couldn't be checked.</summary>
    public int? WithoutClaim { get; init; }

    public string? UnavailableReason { get; init; }
}

public sealed class AuthorizationQueueInsight
{
    public int Total { get; init; }
    public int Expedited { get; init; }
    public List<AuthorizationCountInsight> ByStatus { get; init; } = new();

    /// <summary>By SLA escalation (None, Warning, Critical, Breach), from the SLA watchdog's clock.</summary>
    public List<AuthorizationCountInsight> BySla { get; init; } = new();
}

public sealed class AuthorizationCountInsight
{
    public string Key { get; init; } = string.Empty;
    public int Count { get; init; }
}

public sealed class AuthorizationDenialReasonInsight
{
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// One Authorization Insights figure as a set of authorizations: the
/// drill-down behind every number. <see cref="AuthorizationInsightSets"/>
/// counts and lists with the same predicate.
/// </summary>
public sealed class AuthorizationInsightCriteria
{
    /// <summary>"period": requests submitted in the period. "queue": open now.</summary>
    public string Scope { get; init; } = AuthorizationInsightSets.PeriodScope;

    /// <summary>"Standard" or "Expedited".</summary>
    public string? RequestType { get; init; }

    /// <summary>"Decided", "Approved", "Denied" or "Open".</summary>
    public string? Outcome { get; init; }

    /// <summary>Decided within the request type's timeframe (true) or not (false).</summary>
    public bool? WithinTimeframe { get; init; }

    /// <summary>Decided by an automated rule (true) or a reviewer (false).</summary>
    public bool? Automated { get; init; }

    public string? DenialReasonCode { get; init; }
    public AuthorizationStatus? Status { get; init; }
    public SlaEscalationLevel? Sla { get; init; }
}

public static class AuthorizationInsightSets
{
    public const string PeriodScope = "period";
    public const string QueueScope = "queue";
    /// <summary>Approved, service period ended in the period, no claim yet (<see cref="Services.Insights.UnusedAuthorizations"/>).</summary>
    public const string UnusedScope = "unused";
    public const string UnspecifiedDenialCode = "UNSPECIFIED";

    public static AuthorizationInsights Build(
        IEnumerable<Authorization> activity, IEnumerable<Authorization> open,
        DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf)
    {
        var activityList = activity.ToList();
        var queue = open.Where(a => lineOfBusiness is null || a.LineOfBusiness == lineOfBusiness).ToList();
        var denied = Cms0057Reporting.InPeriod(activityList, from, to, lineOfBusiness)
            .Where(a => a.Status == AuthorizationStatus.Denied);

        return new AuthorizationInsights
        {
            AsOf = asOf, From = from, To = to, LineOfBusiness = lineOfBusiness,
            Reporting = Cms0057Reporting.ComputeMetrics(activityList, from, to, lineOfBusiness),
            Queue = new AuthorizationQueueInsight
            {
                Total = queue.Count,
                Expedited = queue.Count(Cms0057Reporting.IsExpedited),
                ByStatus = queue.GroupBy(a => a.Status).OrderBy(g => g.Key)
                    .Select(g => new AuthorizationCountInsight { Key = g.Key.ToString(), Count = g.Count() }).ToList(),
                BySla = Enum.GetValues<SlaEscalationLevel>()
                    .Select(level => new AuthorizationCountInsight { Key = level.ToString(), Count = queue.Count(a => SlaLevel(a) == level) })
                    .ToList(),
            },
            DenialReasons = denied
                .GroupBy(a => string.IsNullOrWhiteSpace(a.DenialReasonCode) ? UnspecifiedDenialCode : a.DenialReasonCode.Trim())
                .Select(g => new AuthorizationDenialReasonInsight
                {
                    Code = g.Key, Count = g.Count(),
                    Description = g.Select(a => a.DenialReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)),
                })
                .OrderByDescending(r => r.Count).ThenBy(r => r.Code)
                .Take(10)
                .ToList(),
        };
    }

    /// <summary>The authorizations a figure counts, newest first.</summary>
    public static IReadOnlyList<Authorization> List(
        AuthorizationInsightCriteria criteria, IEnumerable<Authorization> activity, IEnumerable<Authorization> open,
        DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, int limit)
    {
        var source = criteria.Scope == QueueScope
            ? open.Where(a => lineOfBusiness is null || a.LineOfBusiness == lineOfBusiness)
            : Cms0057Reporting.InPeriod(activity, from, to, lineOfBusiness);
        return source.Where(a => Matches(a, criteria))
            .OrderByDescending(a => a.SubmittedDate)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();
    }

    public static bool Matches(Authorization a, AuthorizationInsightCriteria c)
    {
        if (c.RequestType is { } type && Cms0057Reporting.IsExpedited(a) != string.Equals(type, "Expedited", StringComparison.OrdinalIgnoreCase))
            return false;
        if (c.Outcome is { } outcome)
        {
            var ok = outcome.ToLowerInvariant() switch
            {
                "decided" => Cms0057Reporting.IsDecided(a.Status),
                "approved" => Cms0057Reporting.IsApproved(a.Status),
                "denied" => a.Status == AuthorizationStatus.Denied,
                "open" => a.Status.IsOpen(),
                _ => false,
            };
            if (!ok) return false;
        }
        if (c.WithinTimeframe is { } within && (!Cms0057Reporting.IsDecided(a.Status) || Cms0057Reporting.DecidedWithinTimeframe(a) != within))
            return false;
        if (c.Automated is { } automated && (!Cms0057Reporting.IsDecided(a.Status) || Cms0057Reporting.IsAutomated(a) != automated))
            return false;
        if (!string.IsNullOrWhiteSpace(c.DenialReasonCode))
        {
            if (a.Status != AuthorizationStatus.Denied) return false;
            var code = string.IsNullOrWhiteSpace(a.DenialReasonCode) ? UnspecifiedDenialCode : a.DenialReasonCode.Trim();
            if (code != c.DenialReasonCode) return false;
        }
        if (c.Status is { } status && a.Status != status) return false;
        if (c.Sla is { } level && SlaLevel(a) != level) return false;
        return true;
    }

    private static SlaEscalationLevel SlaLevel(Authorization a) => SlaWatchdogService.ComputeSlaStatus(a).EscalationLevel;
}
