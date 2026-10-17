namespace AppealsService.Models;

/// <summary>
/// Appeals &amp; Grievances Insights (GET /api/appeals/insights): aggregates
/// only. Timeliness is measured against <see cref="Appeal.TargetResponseDate"/>,
/// the regulatory deadline <c>AppealResponseDeadlinePolicy</c> sets (by line
/// of business, type, level and urgency) and only the one-time extension moves.
/// </summary>
public sealed class AppealInsights
{
    public DateTime AsOf { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public LineOfBusiness? LineOfBusiness { get; init; }

    /// <summary>Received in the period.</summary>
    public AppealReceivedInsight Received { get; init; } = new();

    /// <summary>Resolved (decided) in the period: timeliness and outcomes by level.</summary>
    public List<AppealLevelInsight> ResolvedByLevel { get; init; } = new();

    /// <summary>Open now, whenever received.</summary>
    public AppealOpenInsight Open { get; init; } = new();

    /// <summary>The denial reasons appealed, for appeals received in the period (top 10).</summary>
    public List<AppealReasonInsight> AppealedDenialReasons { get; init; } = new();
}

public sealed class AppealReceivedInsight
{
    public int Total { get; init; }
    public int Appeals { get; init; }
    public int Grievances { get; init; }
    public int Expedited { get; init; }

    /// <summary>Received at external review (IRE / independent review / State Fair Hearing): cases sent outside the plan.</summary>
    public int ExternalReview { get; init; }
}

public sealed class AppealLevelInsight
{
    /// <summary>FirstLevel, SecondLevel or ExternalReview; "Grievance" for grievances (any level).</summary>
    public string Level { get; init; } = string.Empty;
    public AppealTimelinessInsight Standard { get; init; } = new();
    public AppealTimelinessInsight Expedited { get; init; } = new();

    /// <summary>Decided in the member's or provider's favor (Approved or PartialApproval). Not reported for grievances.</summary>
    public int Overturned { get; init; }
    public int Upheld { get; init; }

    /// <summary>Overturned of decided (Approved, PartialApproval, Denied), 0–100; null when none decided.</summary>
    public double? OverturnPercent { get; init; }

    /// <summary>Closed without a decision (withdrawn, expired, administrative error, other).</summary>
    public int ClosedWithoutDecision { get; init; }
}

public sealed class AppealTimelinessInsight
{
    /// <summary>Decided in the period.</summary>
    public int Resolved { get; init; }
    public int OnTime { get; init; }
    public int Late { get; init; }

    /// <summary>Of the resolved, those whose deadline was extended (on time against the extended deadline counts as on time).</summary>
    public int Extended { get; init; }

    public double? OnTimePercent { get; init; }
}

public sealed class AppealOpenInsight
{
    public int Total { get; init; }
    public int Overdue { get; init; }
    public int DueWithin7Days { get; init; }
    public int PendingInfo { get; init; }
    public int Expedited { get; init; }
    public List<AppealCountInsight> ByLevel { get; init; } = new();
}

public sealed class AppealCountInsight
{
    public string Key { get; init; } = string.Empty;
    public int Count { get; init; }
}

public sealed class AppealReasonInsight
{
    /// <summary>The original denial's reason code (CARC); the free-text reason is never aggregated.</summary>
    public string Code { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>
/// The drill-down behind any Appeals Insights figure. <see cref="AppealInsightSets"/>
/// counts and lists with the same predicate.
/// </summary>
public sealed class AppealInsightCriteria
{
    /// <summary>"received", "resolved" (in the period) or "open" (now).</summary>
    public string Scope { get; init; } = AppealInsightSets.ReceivedScope;

    /// <summary>"Appeal" or "Grievance".</summary>
    public string? Kind { get; init; }

    /// <summary>FirstLevel, SecondLevel or ExternalReview (appeals only); "Grievance" for grievances.</summary>
    public string? Level { get; init; }

    public bool? Expedited { get; init; }
    public bool? OnTime { get; init; }
    public bool? Extended { get; init; }

    /// <summary>"Overturned", "Upheld", "Decided" or "ClosedWithoutDecision".</summary>
    public string? Outcome { get; init; }

    public bool? Overdue { get; init; }
    public bool? DueWithin7Days { get; init; }
    public bool? PendingInfo { get; init; }
    public bool? ExternalReview { get; init; }
    public string? DenialReasonCode { get; init; }
}

public static class AppealInsightSets
{
    public const string ReceivedScope = "received";
    public const string ResolvedScope = "resolved";
    public const string OpenScope = "open";
    public const string GrievanceLevel = "Grievance";
    public const string UnspecifiedCode = "UNSPECIFIED";

    private static readonly AppealClosureReasonCode[] DecidedCodes =
        [AppealClosureReasonCode.Approved, AppealClosureReasonCode.PartialApproval, AppealClosureReasonCode.Denied];

    public static bool IsGrievance(Appeal a) => a.AppealType == AppealType.Grievance;
    public static bool IsExternal(Appeal a) => a.AppealLevel == AppealLevel.ExternalReview || a.AppealType == AppealType.ExternalReview;
    public static string LevelKey(Appeal a) => IsGrievance(a) ? GrievanceLevel : (IsExternal(a) ? nameof(AppealLevel.ExternalReview) : a.AppealLevel.ToString());
    public static DateTime ReceivedAt(Appeal a) => a.ReceivedDate ?? a.SubmittedDate;
    public static DateTime? DecidedAt(Appeal a) => a.DecisionDate ?? a.ClosedAt;

    /// <summary>Closed with a decision (Approved, PartialApproval, Denied).</summary>
    public static bool IsDecided(Appeal a) =>
        a.Status == AppealStatus.Closed && a.ClosureReasonCode is { } c && DecidedCodes.Contains(c);

    public static bool IsOverturned(Appeal a) =>
        IsDecided(a) && a.ClosureReasonCode is AppealClosureReasonCode.Approved or AppealClosureReasonCode.PartialApproval;

    /// <summary>Decided on or before the (possibly extended) regulatory deadline; a case with no deadline is not counted on time.</summary>
    public static bool IsOnTime(Appeal a) =>
        IsDecided(a) && DecidedAt(a) is { } d && a.TargetResponseDate is { } due && d <= due;

    public static bool IsOpen(Appeal a) => a.Status is AppealStatus.Submitted or AppealStatus.InReview or AppealStatus.PendingInfo;
    public static bool IsOverdue(Appeal a, DateTime asOf) => IsOpen(a) && a.TargetResponseDate is { } due && due <= asOf;
    public static bool IsDueWithin7Days(Appeal a, DateTime asOf) =>
        IsOpen(a) && a.TargetResponseDate is { } due && due > asOf && due <= asOf.AddDays(7);

    public static AppealInsights Build(IEnumerable<Appeal> activity, IEnumerable<Appeal> open,
        DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf)
    {
        var all = activity.Where(a => InLob(a, lineOfBusiness)).ToList();
        var received = all.Where(a => InRange(ReceivedAt(a), from, to)).ToList();
        var resolved = all.Where(a => IsClosedIn(a, from, to)).ToList();
        var queue = open.Where(a => InLob(a, lineOfBusiness) && IsOpen(a)).ToList();

        return new AppealInsights
        {
            AsOf = asOf, From = from, To = to, LineOfBusiness = lineOfBusiness,
            Received = new AppealReceivedInsight
            {
                Total = received.Count,
                Appeals = received.Count(a => !IsGrievance(a)),
                Grievances = received.Count(IsGrievance),
                Expedited = received.Count(a => a.IsUrgent),
                ExternalReview = received.Count(a => !IsGrievance(a) && IsExternal(a)),
            },
            ResolvedByLevel = new[] { nameof(AppealLevel.FirstLevel), nameof(AppealLevel.SecondLevel), nameof(AppealLevel.ExternalReview), GrievanceLevel }
                .Select(level =>
                {
                    var atLevel = resolved.Where(a => LevelKey(a) == level).ToList();
                    var decided = atLevel.Where(IsDecided).ToList();
                    var overturned = level == GrievanceLevel ? 0 : decided.Count(IsOverturned);
                    return new AppealLevelInsight
                    {
                        Level = level,
                        Standard = Timeliness(decided.Where(a => !a.IsUrgent).ToList()),
                        Expedited = Timeliness(decided.Where(a => a.IsUrgent).ToList()),
                        Overturned = overturned,
                        Upheld = level == GrievanceLevel ? 0 : decided.Count - overturned,
                        OverturnPercent = level == GrievanceLevel || decided.Count == 0 ? null : Percent(overturned, decided.Count),
                        ClosedWithoutDecision = atLevel.Count(a => !IsDecided(a)),
                    };
                })
                .ToList(),
            Open = new AppealOpenInsight
            {
                Total = queue.Count,
                Overdue = queue.Count(a => IsOverdue(a, asOf)),
                DueWithin7Days = queue.Count(a => IsDueWithin7Days(a, asOf)),
                PendingInfo = queue.Count(a => a.Status == AppealStatus.PendingInfo),
                Expedited = queue.Count(a => a.IsUrgent),
                ByLevel = queue.GroupBy(LevelKey).OrderBy(g => g.Key)
                    .Select(g => new AppealCountInsight { Key = g.Key, Count = g.Count() }).ToList(),
            },
            AppealedDenialReasons = received.Where(a => !IsGrievance(a))
                .GroupBy(ReasonCode)
                // Codes only: DenialReason is free text, encrypted at rest, and may hold PHI.
                .Select(g => new AppealReasonInsight { Code = g.Key, Count = g.Count() })
                .OrderByDescending(r => r.Count).ThenBy(r => r.Code)
                .Take(10)
                .ToList(),
        };
    }

    public static IReadOnlyList<Appeal> List(AppealInsightCriteria c, IEnumerable<Appeal> activity, IEnumerable<Appeal> open,
        DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf, int limit)
    {
        IEnumerable<Appeal> source = c.Scope switch
        {
            OpenScope => open.Where(IsOpen),
            ResolvedScope => activity.Where(a => IsClosedIn(a, from, to)),
            _ => activity.Where(a => InRange(ReceivedAt(a), from, to)),
        };
        return source.Where(a => InLob(a, lineOfBusiness) && Matches(a, c, asOf))
            .OrderByDescending(ReceivedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();
    }

    public static bool Matches(Appeal a, AppealInsightCriteria c, DateTime asOf)
    {
        if (c.Kind is { } kind && IsGrievance(a) != string.Equals(kind, "Grievance", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Level is { } level && LevelKey(a) != level) return false;
        if (c.Expedited is { } urgent && a.IsUrgent != urgent) return false;
        if (c.OnTime is { } onTime && (!IsDecided(a) || IsOnTime(a) != onTime)) return false;
        if (c.Extended is { } extended && (!IsDecided(a) || (a.DeadlineExtension is not null) != extended)) return false;
        if (c.Outcome is { } outcome)
        {
            var ok = outcome.ToLowerInvariant() switch
            {
                "overturned" => !IsGrievance(a) && IsOverturned(a),
                "upheld" => !IsGrievance(a) && IsDecided(a) && !IsOverturned(a),
                "decided" => IsDecided(a),
                "closedwithoutdecision" => a.Status == AppealStatus.Closed && !IsDecided(a),
                _ => false,
            };
            if (!ok) return false;
        }
        if (c.Overdue is { } overdue && IsOverdue(a, asOf) != overdue) return false;
        if (c.DueWithin7Days is { } due && IsDueWithin7Days(a, asOf) != due) return false;
        if (c.PendingInfo is { } pending && (a.Status == AppealStatus.PendingInfo) != pending) return false;
        if (c.ExternalReview is { } external && (!IsGrievance(a) && IsExternal(a)) != external) return false;
        if (!string.IsNullOrWhiteSpace(c.DenialReasonCode) && (IsGrievance(a) || ReasonCode(a) != c.DenialReasonCode)) return false;
        return true;
    }

    private static AppealTimelinessInsight Timeliness(IReadOnlyList<Appeal> decided)
    {
        var onTime = decided.Count(IsOnTime);
        return new AppealTimelinessInsight
        {
            Resolved = decided.Count,
            OnTime = onTime,
            Late = decided.Count - onTime,
            Extended = decided.Count(a => a.DeadlineExtension is not null),
            OnTimePercent = decided.Count == 0 ? null : Percent(onTime, decided.Count),
        };
    }

    private static bool IsClosedIn(Appeal a, DateTime from, DateTime to) =>
        a.Status == AppealStatus.Closed && DecidedAt(a) is { } d && InRange(d, from, to);

    private static bool InRange(DateTime d, DateTime from, DateTime to) => d >= from && d <= to;
    private static bool InLob(Appeal a, LineOfBusiness? lob) => lob is null || a.LineOfBusiness == lob;
    private static string ReasonCode(Appeal a) => string.IsNullOrWhiteSpace(a.DenialReasonCode) ? UnspecifiedCode : a.DenialReasonCode.Trim();
    private static double Percent(int part, int whole) => Math.Round(100.0 * part / whole, 1);
}
