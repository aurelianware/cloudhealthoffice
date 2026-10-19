using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class ClaimSearchResult
{
    public List<ClaimSummary> Claims { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
    public decimal TotalChargeAmount { get; set; }
    public decimal TotalAllowedAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendingCount { get; set; }

    /// <summary>
    /// True when the service returned no aggregates and the totals above were
    /// computed from the current page only.
    /// </summary>
    [JsonIgnore]
    public bool TotalsArePageScoped { get; private set; }

    /// <summary>
    /// claims-service search returns a page and a total count only. When no
    /// aggregates came back, compute them from this page and say so.
    /// </summary>
    public void FillPageTotalsIfMissing()
    {
        var hasAggregates = TotalChargeAmount != 0 || TotalAllowedAmount != 0 || TotalPaidAmount != 0
            || ApprovedCount != 0 || DeniedCount != 0 || PendingCount != 0;
        if (hasAggregates || Claims.Count == 0)
        {
            return;
        }

        TotalChargeAmount = Claims.Sum(c => c.TotalChargeAmount);
        TotalAllowedAmount = Claims.Sum(c => c.AllowedAmount);
        TotalPaidAmount = Claims.Sum(c => c.PaidAmount);
        ApprovedCount = Claims.Count(c => ClaimStatusDisplay.IsPayOutcome(c.Status));
        DeniedCount = Claims.Count(c => ClaimStatusDisplay.IsDenied(c.Status));
        PendingCount = Claims.Count(c => c.Status is "Pended" or "Pending" or "InAdjudication" or "In Adjudication" or "Submitted" or "Received");
        TotalsArePageScoped = true;
    }
}
