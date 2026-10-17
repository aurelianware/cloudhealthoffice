using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationContractRepository
{
    Task<CapitationContract?> GetByIdAsync(string id);
    Task<CapitationContract?> GetByProviderNpiAsync(string npi);
    Task<IEnumerable<CapitationContract>> GetActiveContractsAsync(LineOfBusiness? lob = null, ContractType? type = null);
    Task<IEnumerable<CapitationContract>> GetByPlanIdAsync(string planId);
    Task<IEnumerable<CapitationContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ContractType? type = null,
        CapitationRateConfigStatus? status = null,
        int page = 1,
        int pageSize = 50);
    Task<CapitationContract> CreateAsync(CapitationContract contract);
    Task<CapitationContract> UpdateAsync(CapitationContract contract);
}
