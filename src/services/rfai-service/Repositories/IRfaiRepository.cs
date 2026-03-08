using RfaiService.Models;

namespace RfaiService.Repositories;

public interface IRfaiRepository
{
    Task<RfaiCase> CreateAsync(RfaiCase rfaiCase);
    Task<RfaiCase?> GetByIdAsync(string id);
    Task<IEnumerable<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber);
    Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase);
}
