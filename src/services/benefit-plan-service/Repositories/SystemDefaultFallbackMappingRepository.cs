using BenefitPlanService.HostedServices;
using BenefitPlanService.Models;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Repositories;

/// <summary>
/// The resolver's read seam over the mapping store. When a tenant has no
/// tenant-default mappings (the admin seed never ran for it), it returns the
/// loaded system-default bundle instead, so claims still resolve to the right
/// service category (a lab test billed from an office is lab, not an office
/// visit). Plan-scoped reads, and tenants with any tenant-default mapping of
/// their own, pass through unchanged. Read only: nothing is written, and
/// <see cref="ServiceCategoryMappingOptions.FallBackToSystemDefaults"/> turns
/// it off.
/// </summary>
public sealed class SystemDefaultFallbackMappingRepository(
    IServiceCategoryMappingRepository inner,
    SystemDefaultMappingSeeder seeder,
    IOptionsMonitor<ServiceCategoryMappingOptions> options) : IServiceCategoryMappingRepository
{
    public async Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        var mappings = await inner.GetMappingsAsync(tenantId, benefitPlanId, ct).ConfigureAwait(false);
        if (benefitPlanId is not null || mappings.Count > 0 || !options.CurrentValue.FallBackToSystemDefaults)
            return mappings;
        return seeder.DefaultMappingsFor(tenantId);
    }
}
