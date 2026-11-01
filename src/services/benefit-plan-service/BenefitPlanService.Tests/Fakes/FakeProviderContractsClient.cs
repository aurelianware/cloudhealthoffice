using BenefitPlanService.Import.Contracts;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>
/// provider-contracts-service as the CHO-mode importer sees it: legal contracts
/// by number, and a pricing projection of one engine contract per plan.
/// </summary>
public sealed class FakeProviderContractsClient : IProviderContractsClient
{
    public Dictionary<string, LegalContractDto> ByNumber { get; } = new();
    public List<string> Calls { get; } = new();
    public bool FailCreate { get; set; }

    public Task<LegalContractDto?> GetByNumberAsync(string tenantId, string contractNumber, CancellationToken ct)
    {
        Calls.Add($"get {contractNumber}");
        return Task.FromResult(ByNumber.GetValueOrDefault(contractNumber));
    }

    public Task<LegalContractDto> CreateAsync(string tenantId, LegalContractDto contract, CancellationToken ct)
    {
        Calls.Add($"create {contract.ContractNumber}");
        if (FailCreate) throw new ProviderContractsCallException("Could not create contract: the caller needs contracts:write in provider-contracts-service (403).");
        contract.Id = $"legal-{contract.ContractNumber}";
        contract.Status = LegalContractStatus.Draft;
        ByNumber[contract.ContractNumber] = contract;
        return Task.FromResult(contract);
    }

    public Task ReplaceTermsAsync(string tenantId, string id, IReadOnlyList<ReimbursementTerm> terms, CancellationToken ct)
    {
        Calls.Add($"terms {id}");
        Find(id).ReimbursementTerms = terms.ToList();
        return Task.CompletedTask;
    }

    public Task ActivateAsync(string tenantId, string id, CancellationToken ct)
    {
        Calls.Add($"activate {id}");
        Find(id).Status = LegalContractStatus.Active;
        return Task.CompletedTask;
    }

    public Task<PricingProjectionDto> GetPricingProjectionAsync(string tenantId, string id, CancellationToken ct)
    {
        Calls.Add($"projection {id}");
        var legal = Find(id);
        return Task.FromResult(new PricingProjectionDto
        {
            LegalContractId = id,
            Contracts = legal.PlanIds.Select(plan => new ProviderContract
            {
                Id = ProviderContract.MakeId(tenantId, legal.ProviderNpi, plan),
                TenantId = tenantId,
                ProviderNpi = legal.ProviderNpi,
                PlanId = plan,
                ContractNumber = legal.ContractNumber,
                NetworkStatus = NetworkStatus.InNetwork,
                EffectiveDate = legal.EffectiveDate,
                Origin = "legal",
                LegalContractId = id,
                ReimbursementTerms = legal.ReimbursementTerms.ToList(),
            }).ToList(),
        });
    }

    private LegalContractDto Find(string id) => ByNumber.Values.Single(c => c.Id == id);
}
