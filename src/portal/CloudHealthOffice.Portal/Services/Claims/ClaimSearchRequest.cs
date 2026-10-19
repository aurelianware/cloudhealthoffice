namespace CloudHealthOffice.Portal.Services;

public class ClaimSearchRequest
{
    public string? RunId { get; set; }
    public string? ClaimNumber { get; set; }
    public string? MemberId { get; set; }
    public string? MemberName { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ClaimType { get; set; } // Professional, Institutional
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public string? Status { get; set; } // Submitted, Received, InAdjudication, Pended, Approved (Pay), Deny, Paid, Denied, Voided, PartiallyPaid (legacy)
    public string? AuthorizationNumber { get; set; }

    // Claims Insights drill-down: the criteria a figure was counted with.
    public bool? Open { get; set; }
    public bool? Adjudicated { get; set; }
    public DateTime? ReceivedAfter { get; set; }
    public DateTime? ReceivedOnOrBefore { get; set; }
    public DateTime? AdjudicatedFrom { get; set; }
    public DateTime? AdjudicatedBefore { get; set; }
    public string? PendCode { get; set; }
    public string? DenialReasonCode { get; set; }
    public bool? AutoAdjudicated { get; set; }
    public string? LineOfBusiness { get; set; }

    public bool HasDrillDown =>
        Open == true || Adjudicated == true || ReceivedAfter.HasValue || ReceivedOnOrBefore.HasValue
        || AdjudicatedFrom.HasValue || AdjudicatedBefore.HasValue || !string.IsNullOrWhiteSpace(PendCode)
        || !string.IsNullOrWhiteSpace(DenialReasonCode) || AutoAdjudicated.HasValue || !string.IsNullOrWhiteSpace(LineOfBusiness);

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? SortBy { get; set; } = "SubmittedDate";
    public string? SortOrder { get; set; } = "Descending";
}
