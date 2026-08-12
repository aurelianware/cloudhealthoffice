using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationStatementRepository
{
    Task<CapitationStatement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId);
    Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null);
    Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status);
    Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync();
    Task<CapitationStatement> CreateAsync(CapitationStatement statement);
    Task<CapitationStatement> UpdateAsync(CapitationStatement statement);
}
