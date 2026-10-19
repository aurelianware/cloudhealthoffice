namespace CloudHealthOffice.Portal.Services;

public class SalesInquiry
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string InquiryType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = "New"; // New, Contacted, Qualified, Closed
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ContactedAt { get; set; }
    public string? Notes { get; set; }
}
