namespace CloudHealthOffice.Portal.Services;

public interface ISalesInquiryService
{
    Task<string> CreateInquiryAsync(CreateSalesInquiryRequest request);
    Task<List<SalesInquiry>> GetInquiriesAsync(string? status = null, int limit = 100);
    Task<SalesInquiry?> GetInquiryByIdAsync(string inquiryId);
    Task UpdateInquiryStatusAsync(string inquiryId, string status, string? notes = null);
}
