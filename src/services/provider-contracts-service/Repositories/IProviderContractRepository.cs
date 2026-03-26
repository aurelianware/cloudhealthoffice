using ProviderContractsService.Models;

namespace ProviderContractsService.Repositories;

public interface IProviderContractRepository
{
    Task<ProviderContract?> GetByIdAsync(string id);
    Task<ProviderContract?> GetByContractNumberAsync(string number);
    Task<IEnumerable<ProviderContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ProviderContractStatus? status = null,
        PaymentMethodology? paymentMethodology = null,
        NetworkParticipationStatus? networkStatus = null,
        int page = 1,
        int pageSize = 50);
    Task<ProviderContract> CreateAsync(ProviderContract contract);
    Task<ProviderContract> UpdateAsync(ProviderContract contract);
}
