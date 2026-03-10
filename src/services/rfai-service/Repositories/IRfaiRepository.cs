using RfaiService.Models;

namespace RfaiService.Repositories;

public interface IRfaiRepository
{
    Task<RfaiCase?> GetByIdAsync(string tenantId, string id);
    Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber);
    Task<RfaiCase> CreateAsync(RfaiCase rfaiCase);
    Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase);
}
