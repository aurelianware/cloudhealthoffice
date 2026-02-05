using EligibilityService.Models;

namespace EligibilityService.Repositories;

public interface IEligibilityRepository
{
    Task<EligibilityInquiry?> GetInquiryByIdAsync(string tenantId, string id);
    Task<EligibilityInquiry?> GetInquiryByControlNumberAsync(string tenantId, string controlNumber);
    Task<List<EligibilityInquiry>> GetInquiriesBySubscriberAsync(string tenantId, string subscriberId, int page, int pageSize);
    Task CreateInquiryAsync(EligibilityInquiry inquiry);
    Task UpdateInquiryAsync(EligibilityInquiry inquiry);
    
    Task<EligibilityResponse?> GetResponseByIdAsync(string tenantId, string id);
    Task<EligibilityResponse?> GetResponseByInquiryIdAsync(string tenantId, string inquiryId);
    Task CreateResponseAsync(EligibilityResponse response);
}
