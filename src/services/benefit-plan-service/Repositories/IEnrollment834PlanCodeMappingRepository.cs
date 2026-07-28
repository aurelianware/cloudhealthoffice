using BenefitPlanService.Models;

namespace BenefitPlanService.Repositories;

/// <summary>
/// Crosswalk store for <see cref="Enrollment834PlanCodeMapping"/> — resolves
/// a trading partner's own 834 plan code to this platform's canonical PlanId.
/// See the model's doc comment for the full rationale.
/// </summary>
public interface IEnrollment834PlanCodeMappingRepository
{
    /// <summary>Looks up the mapping for an exact (group, insurance line, external code) triple. Null when unmapped.</summary>
    Task<Enrollment834PlanCodeMapping?> ResolveAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default);

    /// <summary>Throws <see cref="DuplicatePlanCodeMappingException"/> when a row already exists for this exact triple.</summary>
    Task<Enrollment834PlanCodeMapping> CreateAsync(
        Enrollment834PlanCodeMapping mapping, CancellationToken ct = default);

    /// <summary>Lists mappings for a tenant, optionally narrowed to one group number.</summary>
    Task<IReadOnlyList<Enrollment834PlanCodeMapping>> ListAsync(
        string tenantId, string? groupNumber, CancellationToken ct = default);

    /// <summary>Returns false when no matching row existed (tenant mismatch counts as not-found).</summary>
    Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="IEnrollment834PlanCodeMappingRepository.CreateAsync"/> when a
/// mapping already exists for the same (tenant, group, insurance line, external code)
/// — the repository's unique index is the source of truth, not a pre-check, so this
/// can surface even under concurrent onboarding writes.
/// </summary>
public sealed class DuplicatePlanCodeMappingException(
    string groupNumber, string insuranceLineCode, string externalPlanCode)
    : Exception($"A mapping already exists for group {groupNumber}, line {insuranceLineCode}, code {externalPlanCode}.")
{
    public string GroupNumber { get; } = groupNumber;
    public string InsuranceLineCode { get; } = insuranceLineCode;
    public string ExternalPlanCode { get; } = externalPlanCode;
}
