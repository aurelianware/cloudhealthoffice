namespace CloudHealthOffice.Portal.Services;

public interface ICoverageService
{
    Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId);
}
