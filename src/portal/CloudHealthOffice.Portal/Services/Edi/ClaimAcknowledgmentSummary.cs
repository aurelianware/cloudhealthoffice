namespace CloudHealthOffice.Portal.Services;

public class ClaimAcknowledgmentSummary
{
    public string AckId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; }
    public string AckStatus { get; set; } = string.Empty; // Accepted, Rejected, Pended
    public string StatusCategoryCode { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public string StatusDescription { get; set; } = string.Empty;
}
