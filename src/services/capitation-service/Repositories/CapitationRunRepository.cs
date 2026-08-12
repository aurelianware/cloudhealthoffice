using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationRunRepository
{
    Task<CapitationRun?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationRun>> SearchAsync(DateTime? from = null, DateTime? to = null, CapitationRunStatus? status = null, LineOfBusiness? lineOfBusiness = null);
    Task<IEnumerable<CapitationRun>> GetByStatusAsync(CapitationRunStatus status);
    Task<CapitationRun> CreateAsync(CapitationRun run);
    Task<CapitationRun> UpdateAsync(CapitationRun run);
}
