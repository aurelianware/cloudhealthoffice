using CloudHealthOffice.FeeScheduleEngine.Models;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// Storage interface for fee schedules and their procedure lines.
///
/// Fee schedules are loaded at adjudication time and should be cached
/// at the service layer (plan configs don't change mid-claim).
/// </summary>
public interface IFeeScheduleRepository
{
    /// <summary>Load a specific fee schedule by ID.</summary>
    Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>
    /// Find the plan's default fee schedule effective on a given date.
    /// Returns null if no default schedule is configured for the plan.
    /// </summary>
    Task<FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default);

    /// <summary>
    /// Look up a single rate line for a procedure code within a schedule.
    ///
    /// Lookup priority:
    ///   1. Exact match: procedureCode + modifier
    ///   2. Base rate:   procedureCode + null modifier
    ///   3. Not found:   return null (caller falls back to UCR/billed charges)
    /// </summary>
    Task<FeeScheduleLine?> GetLineAsync(
        string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default);

    /// <summary>Create or replace a fee schedule (admin / import operations).</summary>
    Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default);

    /// <summary>List all schedules for a tenant (for admin UI).</summary>
    Task<IReadOnlyList<FeeSchedule>> ListAsync(
        string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default);
}

/// <summary>
/// Storage interface for provider contracts.
/// </summary>
public interface IProviderContractRepository
{
    /// <summary>
    /// Find the active contract for a provider/plan on a service date.
    ///
    /// Tries NPI first, then falls back to GroupTin if provided.
    /// Returns null if the provider has no contract (treat as out-of-network).
    /// </summary>
    Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate,
        CancellationToken ct = default);

    /// <summary>Create or replace a provider contract.</summary>
    Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default);

    /// <summary>List contracts for a provider across all plans (for admin UI).</summary>
    Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default);
}
