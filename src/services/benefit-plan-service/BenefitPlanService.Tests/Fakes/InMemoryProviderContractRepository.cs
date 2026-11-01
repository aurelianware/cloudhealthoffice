using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>Engine contract store for unit tests; revision-checked like the MongoDB store.</summary>
public sealed class InMemoryProviderContractRepository : IProviderContractRepository
{
    private readonly Dictionary<(string, string), ProviderContract> _docs = new();
    public IReadOnlyList<ProviderContract> Stored => _docs.Values.ToList();
    public int Writes { get; private set; }

    public Task<ProviderContract?> GetContractAsync(string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult(_docs.Values.FirstOrDefault(c => c.TenantId == tenantId && c.ProviderNpi == providerNpi
            && c.PlanId == planId && c.EffectiveDate <= serviceDate && (c.TermDate is null || c.TermDate > serviceDate)));

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
    {
        var stored = _docs.GetValueOrDefault((contract.TenantId, contract.Id))?.Revision ?? 0;
        if (stored != contract.Revision)
            throw new FeeScheduleConcurrencyException("Provider contract", contract.Id, contract.Revision);
        contract.Revision++;
        _docs[(contract.TenantId, contract.Id)] = contract;
        Writes++;
        return Task.FromResult(contract);
    }

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProviderContract>>(
            _docs.Values.Where(c => c.TenantId == tenantId && c.ProviderNpi == providerNpi).ToList());
}
