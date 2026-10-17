namespace CloudHealthOffice.Portal.Services;

/// <summary>Appeals &amp; Grievances Insights from appeals-service (GET /api/appeals/insights); aggregates only.</summary>
public sealed class AppealInsightsView
{
    public DateTime AsOf { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? LineOfBusiness { get; set; }
    public AppealReceivedView Received { get; set; } = new();
    public List<AppealLevelView> ResolvedByLevel { get; set; } = new();
    public AppealOpenView Open { get; set; } = new();
    public List<AppealReasonView> AppealedDenialReasons { get; set; } = new();
}

public sealed class AppealReceivedView
{
    public int Total { get; set; }
    public int Appeals { get; set; }
    public int Grievances { get; set; }
    public int Expedited { get; set; }
    public int ExternalReview { get; set; }
}

public sealed class AppealLevelView
{
    public string Level { get; set; } = string.Empty;
    public AppealTimelinessView Standard { get; set; } = new();
    public AppealTimelinessView Expedited { get; set; } = new();
    public int Overturned { get; set; }
    public int Upheld { get; set; }
    public double? OverturnPercent { get; set; }
    public int ClosedWithoutDecision { get; set; }

    public int Resolved => Standard.Resolved + Expedited.Resolved;
    public int OnTime => Standard.OnTime + Expedited.OnTime;
}

public sealed class AppealTimelinessView
{
    public int Resolved { get; set; }
    public int OnTime { get; set; }
    public int Late { get; set; }
    public int Extended { get; set; }
    public double? OnTimePercent { get; set; }
}

public sealed class AppealOpenView
{
    public int Total { get; set; }
    public int Overdue { get; set; }
    public int DueWithin7Days { get; set; }
    public int PendingInfo { get; set; }
    public int Expedited { get; set; }
    public List<AppealCountView> ByLevel { get; set; } = new();
}

public sealed class AppealCountView
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class AppealReasonView
{
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// The appeals-list URL for an Insights figure: the criteria it was counted
/// with (appeals-service lists them with the same predicate).
/// </summary>
public static class AppealDrillDown
{
    public const string UnspecifiedCode = "UNSPECIFIED";

    public static readonly string[] Keys =
        ["scope", "from", "to", "lob", "kind", "level", "expedited", "onTime", "extended", "outcome", "overdue", "dueWithin7Days", "pendingInfo", "externalReview", "denialReasonCode"];

    public static string Received(DateTime from, DateTime to, string? lob, string? kind = null, bool? expedited = null, bool? externalReview = null, string? denialReasonCode = null) =>
        Url(("scope", "received"), ("from", Iso(from)), ("to", Iso(to)), ("lob", lob), ("kind", kind), ("expedited", B(expedited)),
            ("externalReview", B(externalReview)), ("denialReasonCode", denialReasonCode));

    public static string Resolved(DateTime from, DateTime to, string? lob, string? level = null, bool? expedited = null, bool? onTime = null, bool? extended = null, string? outcome = null) =>
        Url(("scope", "resolved"), ("from", Iso(from)), ("to", Iso(to)), ("lob", lob), ("level", level), ("expedited", B(expedited)),
            ("onTime", B(onTime)), ("extended", B(extended)), ("outcome", outcome));

    public static string Open(string? lob, bool? overdue = null, bool? dueWithin7Days = null, bool? pendingInfo = null, string? level = null) =>
        Url(("scope", "open"), ("lob", lob), ("overdue", B(overdue)), ("dueWithin7Days", B(dueWithin7Days)), ("pendingInfo", B(pendingInfo)), ("level", level));

    private static string? B(bool? value) => value?.ToString().ToLowerInvariant();
    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string Url(params (string Key, string? Value)[] parts) =>
        "/appeals?" + string.Join("&", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
}
