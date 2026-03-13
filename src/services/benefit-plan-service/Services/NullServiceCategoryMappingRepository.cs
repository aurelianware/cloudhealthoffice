using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;

namespace BenefitPlanService.Services;

/// <summary>
/// No-op IServiceCategoryMappingRepository. Returns empty lists so that
/// ServiceCategoryResolver falls back to its built-in POS-code inference
/// (POS 11 → "98" Professional Visit, etc.) without needing seeded mapping rows.
/// Replace with ChoServiceCategoryMappingRepository once mappings are authored.
/// </summary>
public class NullServiceCategoryMappingRepository : IServiceCategoryMappingRepository
{
    public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        IReadOnlyList<ServiceCategoryMapping> empty = [];
        return Task.FromResult(empty);
    }
}
