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
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? SortBy { get; set; } = "SubmittedDate";
    public string? SortOrder { get; set; } = "Descending";
}
