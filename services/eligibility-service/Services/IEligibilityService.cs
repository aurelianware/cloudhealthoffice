using EligibilityService.Models;

namespace EligibilityService.Services;

public interface IEligibilityService
{
    Task<EligibilityResponse> ProcessInquiryAsync(EligibilityInquiry inquiry);
    Task<(bool IsActive, string StatusCode, string CoverageLevel, string Message)> QuickEligibilityCheckAsync(
        string tenantId, string subscriberId, string? groupNumber, DateTime serviceDate);
    Task<List<EligibilityBenefit>> GetBenefitDetailsAsync(
        string tenantId, string subscriberId, string? serviceType, DateTime serviceDate);
    Task<(DeductibleInfo? Deductible, OutOfPocketInfo? OutOfPocket)> GetAccumulationAsync(
        string tenantId, string subscriberId);
    Task<List<EligibilityInquiry>> GetInquiryHistoryAsync(
        string tenantId, string subscriberId, int page, int pageSize);
    Task<(bool Required, string Reason)> CheckAuthRequirementAsync(
        string tenantId, string subscriberId, string serviceTypeCode, string? procedureCode);
}
