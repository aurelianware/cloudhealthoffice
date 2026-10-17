namespace CloudHealthOffice.Portal.Services;

/// <summary>Authorization Insights from authorization-service (GET /api/authorizations/insights); aggregates only.</summary>
public sealed class AuthorizationInsightsView
{
    public DateTime AsOf { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? LineOfBusiness { get; set; }

    /// <summary>The CMS-0057 reporting figures for requests submitted in the period.</summary>
    public PriorAuthMetrics Reporting { get; set; } = new();

    public AuthorizationQueueView Queue { get; set; } = new();
    public List<AuthorizationDenialReasonView> DenialReasons { get; set; } = new();
    public AuthorizationUnusedView Unused { get; set; } = new();
}

/// <summary>Approved authorizations whose service period ended in the period, and those with no claim yet.</summary>
public sealed class AuthorizationUnusedView
{
    public int Ended { get; set; }

    /// <summary>Null when claim usage couldn't be checked (see <see cref="UnavailableReason"/>).</summary>
    public int? WithoutClaim { get; set; }

    public string? UnavailableReason { get; set; }
}

public sealed class AuthorizationQueueView
{
    public int Total { get; set; }
    public int Expedited { get; set; }
    public List<AuthorizationCountView> ByStatus { get; set; } = new();
    public List<AuthorizationCountView> BySla { get; set; } = new();
}

public sealed class AuthorizationCountView
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class AuthorizationDenialReasonView
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// The authorizations-list URL for an Insights figure: the criteria the
/// figure was counted with (authorization-service lists them with the same
/// predicate), so the list it opens holds exactly those authorizations.
/// </summary>
public static class AuthorizationDrillDown
{
    public const string UnspecifiedCode = "UNSPECIFIED";

    /// <summary>The query keys the Authorizations page passes through to <c>insights/list</c>.</summary>
    public static readonly string[] Keys = ["scope", "from", "to", "lob", "requestType", "outcome", "withinTimeframe", "automated", "denialReasonCode", "status", "sla"];

    public static string Period(DateTime from, DateTime to, string? lob, string? requestType = null, string? outcome = null,
        bool? withinTimeframe = null, bool? automated = null, string? denialReasonCode = null) =>
        Url(("scope", "period"), ("from", Iso(from)), ("to", Iso(to)), ("lob", lob), ("requestType", requestType), ("outcome", outcome),
            ("withinTimeframe", withinTimeframe?.ToString().ToLowerInvariant()), ("automated", automated?.ToString().ToLowerInvariant()),
            ("denialReasonCode", denialReasonCode));

    /// <summary>Approved, service period ended in the period, no claim yet.</summary>
    public static string Unused(DateTime from, DateTime to, string? lob) =>
        Url(("scope", "unused"), ("from", Iso(from)), ("to", Iso(to)), ("lob", lob));

    public static string Queue(string? lob, string? status = null, string? sla = null) =>
        Url(("scope", "queue"), ("lob", lob), ("status", status), ("sla", sla));

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string Url(params (string Key, string? Value)[] parts) =>
        "/authorizations?" + string.Join("&", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
}
