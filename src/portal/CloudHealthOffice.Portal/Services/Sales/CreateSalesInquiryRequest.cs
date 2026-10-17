namespace CloudHealthOffice.Portal.Services;

public class CreateSalesInquiryRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string InquiryType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "Contact Sales Page";
}
