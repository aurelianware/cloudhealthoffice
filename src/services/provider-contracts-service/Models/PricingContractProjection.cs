using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractsService.Models;

/// <summary>
/// The legal contract as the pricing engine's contract (ADR 016 §2 and §4).
///
/// <para>
/// This is the link the ADR says is missing: today the legal agreement and the
/// document the engine prices from are two unrelated records, and nothing
/// derives one from the other. This projection is that derivation, and it is
/// deliberately a pure function of the legal contract — the phase 3 backfill
/// writes exactly what it returns, and the phase 4 dual-read window regenerates
/// the engine contract from it after every edit, so the two cannot drift. Until
/// then it is readable over the API, which is how a reviewer can see what a
/// contract would price as before anything is written.
/// </para>
///
/// <para>
/// It projects no TIN. Pricing needs the NPI, the plan and the terms; the
/// engine's store is read by more services than the contract API is, and the
/// ADR's own risk register keeps TIN to the one masked reader it has today.
/// </para>
/// </summary>
public static class PricingContractProjection
{
    /// <summary>
    /// The engine contracts this legal contract projects to — one per plan,
    /// because the engine looks a contract up by a single plan id
    /// (<see cref="EngineModels.ProviderContract.MakeId"/>) while the legal
    /// contract covers a list of them — together with everything a reviewer or
    /// the backfill's conflict report needs to know about the projection.
    /// </summary>
    public static PricingContractProjectionResult Project(ProviderContract contract)
    {
        var warnings = new List<string>();

        if (contract.Status != ProviderContractStatus.Active)
        {
            warnings.Add(
                $"the contract is {contract.Status}, not Active; a projection of it must not be made " +
                "effective for pricing until the contract is");
        }

        if (contract.ReimbursementTerms.Count == 0)
        {
            warnings.Add(
                "the contract names no reimbursement terms, so the projection prices nothing: every claim " +
                "line would fall through to the plan default schedule");
        }

        var networkStatus = MapNetworkStatus(contract.NetworkStatus);
        if (contract.NetworkStatus == NetworkParticipationStatus.TieredException)
        {
            warnings.Add(
                "network participation is TieredException, which the engine's NetworkStatus has no value " +
                $"for; the projection says {networkStatus} and the tiering must be carried by the " +
                "reimbursement terms' scopes");
        }

        // No plan ids means every plan in the line of business, which only the
        // plan catalogue can enumerate. Project one contract with no plan id so
        // the shape is still reviewable, and say what has to happen to it.
        var planIds = contract.PlanIds.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (planIds.Count == 0)
        {
            warnings.Add(
                $"the contract names no plan ids, so it covers every {contract.LineOfBusiness} plan; the " +
                "projection below has no plan id and must be expanded to one contract per plan against " +
                "the plan catalogue before it is written");
            planIds.Add(string.Empty);
        }

        var contracts = planIds
            .Select(planId => ForPlan(contract, planId, networkStatus))
            .ToList();

        return new PricingContractProjectionResult
        {
            LegalContractId = contract.Id,
            ContractNumber = contract.ContractNumber,
            Contracts = contracts,
            Warnings = warnings,
        };
    }

    private static EngineModels.ProviderContract ForPlan(
        ProviderContract contract, string planId, EngineDomain.NetworkStatus networkStatus) => new()
        {
            // Deterministic, so a re-run of the backfill updates rather than
            // duplicates — the engine's own key for a provider on a plan.
            Id = EngineModels.ProviderContract.MakeId(contract.TenantId, contract.ProviderNPI, planId),
            TenantId = contract.TenantId,
            ProviderNpi = contract.ProviderNPI,
            PlanId = planId,
            NetworkStatus = networkStatus,
            EffectiveDate = contract.EffectiveDate,
            TermDate = contract.TerminationDate,

            ReimbursementTerms = contract.ReimbursementTerms,

            // The terms are the rate sheet, so the legacy fields stay empty:
            // a contract that carries terms is priced by them and these are not
            // read (ADR 016 phase 2).
            FeeScheduleId = string.Empty,
            ContractLines = [],

            LegalContractId = contract.Id,
            ContractNumber = contract.ContractNumber,
            LineOfBusiness = contract.LineOfBusiness.ToString(),
            PlanIds = contract.PlanIds.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList(),
            Origin = "legal",

            // Lesser-of-billed is a contract provision the legal record does not
            // carry a field for yet; a term can set it per rate until it does.
            LesserOfBilledCharges = false,
        };

    /// <summary>
    /// Network participation as the engine's network status. The two enums do
    /// not line up — the ADR lists the mismatch as a backfill risk — so the
    /// mapping is here, named, rather than guessed at in three places.
    /// </summary>
    private static EngineDomain.NetworkStatus MapNetworkStatus(NetworkParticipationStatus status)
        => status switch
        {
            NetworkParticipationStatus.Participating => EngineDomain.NetworkStatus.InNetwork,
            NetworkParticipationStatus.NonParticipating => EngineDomain.NetworkStatus.OutOfNetwork,
            // In-network for the services the contract names, which the terms
            // carry; the status itself is the in-network one.
            NetworkParticipationStatus.TieredException => EngineDomain.NetworkStatus.InNetwork,
            _ => EngineDomain.NetworkStatus.Unknown,
        };
}

/// <summary>What a legal contract projects to, and what a reviewer should know about it.</summary>
public class PricingContractProjectionResult
{
    /// <summary>The legal contract this was projected from.</summary>
    public string LegalContractId { get; set; } = string.Empty;

    /// <summary>Its user-facing contract number.</summary>
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>One engine contract per plan the legal contract covers.</summary>
    public List<EngineModels.ProviderContract> Contracts { get; set; } = new();

    /// <summary>
    /// Everything about this projection that needs a person's judgement before
    /// it is written: a contract that is not active, one with no terms, one
    /// whose plan scope has to be expanded, a network status with no engine
    /// equivalent. Empty means the projection is ready to write as it stands.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
