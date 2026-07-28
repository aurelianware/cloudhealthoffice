using BenefitPlanService.Models;

namespace BenefitPlanService.Repositories;

/// <summary>
/// No-op backend used when the service is running on Cosmos rather than
/// Mongo — this mapping only has a Mongo-backed implementation so far (every
/// recently-added capability in this repo has landed Mongo-only). Resolve
/// always misses and writes are rejected outright rather than silently
/// no-opping, so a Cosmos-configured deployment fails loudly instead of
/// quietly losing plan-code mappings.
/// </summary>
public sealed class NullEnrollment834PlanCodeMappingRepository : IEnrollment834PlanCodeMappingRepository
{
    public Task<Enrollment834PlanCodeMapping?> ResolveAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default) => Task.FromResult<Enrollment834PlanCodeMapping?>(null);

    public Task<Enrollment834PlanCodeMapping> CreateAsync(
        Enrollment834PlanCodeMapping mapping, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Enrollment834PlanCodeMapping writes require the Mongo backend (MongoDb:ConnectionString).");

    public Task<IReadOnlyList<Enrollment834PlanCodeMapping>> ListAsync(
        string tenantId, string? groupNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Enrollment834PlanCodeMapping>>(Array.Empty<Enrollment834PlanCodeMapping>());

    public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(false);
}
