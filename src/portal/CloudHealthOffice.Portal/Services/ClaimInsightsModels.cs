namespace CloudHealthOffice.Portal.Services;

/// <summary>Claims Insights from claims-service (GET /api/claims/insights); aggregates only.</summary>
public sealed class ClaimInsightsView
{
    public DateTime AsOf { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? LineOfBusiness { get; set; }
    public ClaimInventoryView Inventory { get; set; } = new();
    public List<ClaimPendReasonView> PendReasons { get; set; } = new();
    public ClaimOutcomeView Outcomes { get; set; } = new();
    public List<ClaimDenialReasonView> DenialReasons { get; set; } = new();
    public ClaimTurnaroundView Turnaround { get; set; } = new();
}

public sealed class ClaimInventoryView
{
    public int Total { get; set; }
    public decimal TotalCharge { get; set; }
    public List<ClaimStatusCountView> ByStatus { get; set; } = new();
    public List<ClaimAgingBucketView> Aging { get; set; } = new();
}

public sealed class ClaimStatusCountView
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Charge { get; set; }
}

public sealed class ClaimAgingBucketView
{
    public string Label { get; set; } = string.Empty;
    public int MinDays { get; set; }
    public int? MaxDays { get; set; }
    public int Count { get; set; }
    public decimal Charge { get; set; }
    public DateTime? ReceivedAfter { get; set; }
    public DateTime? ReceivedOnOrBefore { get; set; }
}

public sealed class ClaimPendReasonView
{
    public string PendCode { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime? OldestPendedAt { get; set; }
}

public sealed class ClaimOutcomeView
{
    public int Adjudicated { get; set; }
    public int AutoAdjudicated { get; set; }
    public decimal? AutoAdjudicationRate { get; set; }
    public List<ClaimStatusCountView> ByStatus { get; set; } = new();
    public decimal TotalCharge { get; set; }
    public decimal TotalPaid { get; set; }
}

public sealed class ClaimDenialReasonView
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Count { get; set; }
}

public sealed class ClaimTurnaroundView
{
    public int Count { get; set; }
    public decimal? AverageDays { get; set; }
    public decimal? MedianDays { get; set; }
    public decimal? P90Days { get; set; }
    public bool Sampled { get; set; }
}

/// <summary>
/// The claims-list URL for an Insights figure: the same criteria the figure
/// was counted with, as query parameters the Claims page reads, so the list
/// it opens holds exactly those claims and the link can be shared.
/// </summary>
public static class ClaimDrillDown
{
    public const string UnspecifiedCode = "UNSPECIFIED";

    public static string Open(string? lineOfBusiness, DateTime? receivedAfter = null, DateTime? receivedOnOrBefore = null, string? status = null) =>
        Url(("open", "true"), ("status", status), ("receivedAfter", Iso(receivedAfter)), ("receivedOnOrBefore", Iso(receivedOnOrBefore)), ("lob", lineOfBusiness));

    public static string Pended(string pendCode, string? lineOfBusiness) =>
        Url(("status", "Pended"), ("pendCode", pendCode), ("lob", lineOfBusiness));

    public static string Adjudicated(DateTime from, DateTime before, string? lineOfBusiness, string? status = null, bool? auto = null) =>
        Url(("adjudicated", "true"), ("status", status), ("adjudicatedFrom", Iso(from)), ("adjudicatedBefore", Iso(before)),
            ("auto", auto?.ToString().ToLowerInvariant()), ("lob", lineOfBusiness));

    public static string Denied(string code, DateTime from, DateTime before, string? lineOfBusiness) =>
        Url(("adjudicated", "true"), ("denialCode", code), ("adjudicatedFrom", Iso(from)), ("adjudicatedBefore", Iso(before)), ("lob", lineOfBusiness));

    private static string? Iso(DateTime? value) => value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string Url(params (string Key, string? Value)[] parts) =>
        "/claims?" + string.Join("&", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
}
