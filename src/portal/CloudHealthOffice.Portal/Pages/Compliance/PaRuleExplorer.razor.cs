using CloudHealthOffice.Portal.Models;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Pages.Compliance;

public partial class PaRuleExplorer
{
    private string? _serviceError;
    private int _activeTab;

    // Search state
    private bool _searchLoading;
    private List<TmppmPaRuleViewModel>? _searchResults;
    private string? _lastSearchCode;

    // Category state
    private bool _categoriesLoading = true;
    private List<PaCategoryGroup> _categories = [];
    private string? _selectedCategory;
    private List<TmppmPaRuleViewModel>? _categoryRules;
    private bool _categoryRulesLoading;

    // Edition state
    private bool _editionLoading = true;
    private TmppmEditionViewModel? _currentEdition;
    private List<TmppmEditionViewModel> _allEditions = [];

    // Diff state
    private bool _diffLoading;
    private TmppmDiffViewModel? _diffResult;

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(
            LoadEditionAsync(),
            LoadCategoriesAsync());
    }

    private async Task LoadEditionAsync()
    {
        try
        {
            _editionLoading = true;
            var editionsTask = RuleService.GetAllEditionsAsync();
            var currentTask = RuleService.GetCurrentEditionAsync();
            await Task.WhenAll(editionsTask, currentTask);
            _allEditions = editionsTask.Result;
            _currentEdition = currentTask.Result;
        }
        catch (Exception ex)
        {
            _serviceError = $"Failed to load edition data: {ex.Message}";
        }
        finally
        {
            _editionLoading = false;
        }
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            _categoriesLoading = true;
            _categories = await RuleService.GetCategoriesAsync();
        }
        catch (Exception ex)
        {
            _serviceError = $"Failed to load categories: {ex.Message}";
        }
        finally
        {
            _categoriesLoading = false;
        }
    }

    private async Task HandleSearchAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        try
        {
            _searchLoading = true;
            _lastSearchCode = code.Trim().ToUpperInvariant();
            _searchResults = await RuleService.SearchByCodeAsync(_lastSearchCode);
        }
        catch (Exception ex)
        {
            _serviceError = $"Search failed: {ex.Message}";
            _searchResults = [];
        }
        finally
        {
            _searchLoading = false;
        }
    }

    private async Task<List<string>> HandleAutocompleteAsync(string prefix)
    {
        try
        {
            return await RuleService.AutocompleteCodeAsync(prefix);
        }
        catch
        {
            return [];
        }
    }

    private async Task HandleCategorySelectedAsync(string category)
    {
        try
        {
            _selectedCategory = category;
            _categoryRulesLoading = true;
            StateHasChanged();
            _categoryRules = await RuleService.GetRulesByCategoryAsync(category);
        }
        catch (Exception ex)
        {
            _serviceError = $"Failed to load rules for {category}: {ex.Message}";
            _categoryRules = [];
        }
        finally
        {
            _categoryRulesLoading = false;
        }
    }

    private async Task HandleDiffRequestedAsync((string from, string to) editions)
    {
        try
        {
            _diffLoading = true;
            StateHasChanged();
            _diffResult = await RuleService.GetDiffAsync(editions.from, editions.to);
        }
        catch (Exception ex)
        {
            _serviceError = $"Failed to load diff: {ex.Message}";
            _diffResult = null;
        }
        finally
        {
            _diffLoading = false;
        }
    }
}
