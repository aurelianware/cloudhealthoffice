namespace CloudHealthOffice.Portal.Services;

public interface IEligibilityService
{
    Task<EligibilityResponse> CheckEligibilityAsync(object request);
}
