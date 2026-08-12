using AuthorizationService.Models;

namespace AuthorizationService.Repositories;

public interface IAuthorizationRepository
{
    Task<Authorization?> GetByIdAsync(string id);
    Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber);
    Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);
    Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null);
    Task<Authorization> CreateAsync(Authorization authorization);
    Task<Authorization> UpdateAsync(Authorization authorization);
    Task DeleteAsync(string id);
}
