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
    /// <summary>Every rate config (any status) whose parent is the provider contract <paramref name="contractId"/>.</summary>
    Task<IEnumerable<CapitationContract>> GetByContractIdAsync(string contractId);

    /// <summary>
    /// Copies the parent provider contract's denormalized fields onto every rate
    /// config of <paramref name="contractId"/> in the request tenant, stamps
    /// <see cref="CapitationRateConfig.LastDenormSyncAt"/> and bumps each
    /// revision, so an edit based on the old copy answers 409 instead of
    /// writing stale parent fields back.
    /// </summary>
    Task<ParentFieldsSyncResult> SyncParentFieldsAsync(string contractId, ParentContractFields fields, DateTime syncedAt);

    Task<CapitationContract> CreateAsync(CapitationContract contract);
    Task<CapitationContract> UpdateAsync(CapitationContract contract);
}
