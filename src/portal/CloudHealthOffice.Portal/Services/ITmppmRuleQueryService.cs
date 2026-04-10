using CloudHealthOffice.Portal.Models;

namespace CloudHealthOffice.Portal.Services;

public interface ITmppmRuleQueryService
{
    Task<List<TmppmPaRuleViewModel>> SearchByCodeAsync(string code, string? tenantId = null, string? state = null);

    Task<List<PaCategoryGroup>> GetCategoriesAsync(string state = "TX");

    Task<List<TmppmPaRuleViewModel>> GetRulesByCategoryAsync(string category, string? tenantId = null, string? state = null);

    Task<TmppmEditionViewModel?> GetCurrentEditionAsync();

    Task<TmppmDiffViewModel?> GetDiffAsync(string fromEdition, string toEdition);

    Task<List<string>> AutocompleteCodeAsync(string prefix, int maxResults = 10);

    Task<List<TmppmEditionViewModel>> GetAllEditionsAsync();
}
