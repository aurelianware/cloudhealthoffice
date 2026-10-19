namespace CloudHealthOffice.Portal.Services;

// ── Provider Contracts ──────────────────────────────────────────────────────

public interface IProviderContractsService
{
    Task<List<ProviderContractSummary>> GetContractsAsync(
        string? npi = null, string? lob = null,
        string? status = null, string? paymentMethodology = null,
        string? networkStatus = null);
    Task<ProviderContractSummary?> GetContractByIdAsync(string id);
    Task<ProviderContractSummary?> GetContractByNumberAsync(string number);
    Task<string> CreateContractAsync(ProviderContractSummary contract);
    Task UpdateContractAsync(string id, ProviderContractSummary contract);
    Task ActivateContractAsync(string id);
    Task SuspendContractAsync(string id, string reason);
    Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null);
    Task ReinstateContractAsync(string id);
    Task AddAmendmentAsync(string id, ContractAmendmentSummary amendment);
    Task SyncChildrenAsync(string id);
    Task<List<string>> GetRateConfigIdsAsync(string id);
}
