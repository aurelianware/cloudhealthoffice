using BenefitPlanService.Models;
using BenefitPlanService.Repositories;

namespace BenefitPlanService.Tests.Fakes;

public sealed class InMemoryEnrollment834PlanCodeMappingRepository : IEnrollment834PlanCodeMappingRepository
{
    private readonly List<Enrollment834PlanCodeMapping> _mappings = [];

    public Task<Enrollment834PlanCodeMapping?> ResolveAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default)
    {
        var match = _mappings.FirstOrDefault(m =>
            m.TenantId == tenantId && m.GroupNumber == groupNumber
            && m.InsuranceLineCode == insuranceLineCode && m.ExternalPlanCode == externalPlanCode);
        return Task.FromResult(match);
    }

    public Task<Enrollment834PlanCodeMapping> CreateAsync(
        Enrollment834PlanCodeMapping mapping, CancellationToken ct = default)
    {
        var conflict = _mappings.Any(m =>
            m.TenantId == mapping.TenantId && m.GroupNumber == mapping.GroupNumber
            && m.InsuranceLineCode == mapping.InsuranceLineCode && m.ExternalPlanCode == mapping.ExternalPlanCode);
        if (conflict)
        {
            throw new DuplicatePlanCodeMappingException(
                mapping.GroupNumber, mapping.InsuranceLineCode, mapping.ExternalPlanCode);
        }

        if (string.IsNullOrEmpty(mapping.Id))
        {
            mapping.Id = Guid.NewGuid().ToString();
        }
        _mappings.Add(mapping);
        return Task.FromResult(mapping);
    }

    public Task<IReadOnlyList<Enrollment834PlanCodeMapping>> ListAsync(
        string tenantId, string? groupNumber, CancellationToken ct = default)
    {
        IEnumerable<Enrollment834PlanCodeMapping> query = _mappings.Where(m => m.TenantId == tenantId);
        if (!string.IsNullOrEmpty(groupNumber))
        {
            query = query.Where(m => m.GroupNumber == groupNumber);
        }
        return Task.FromResult<IReadOnlyList<Enrollment834PlanCodeMapping>>(query.ToList());
    }

    public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var removed = _mappings.RemoveAll(m => m.TenantId == tenantId && m.Id == id) > 0;
        return Task.FromResult(removed);
    }
}
