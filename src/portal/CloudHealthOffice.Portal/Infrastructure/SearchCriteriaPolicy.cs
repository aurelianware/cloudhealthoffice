using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// When the record search pages (claims, members, providers, authorizations)
/// require at least one criterion before searching. Requiring criteria is the
/// minimum-necessary control for PHI: users find the record they need rather
/// than browsing lists of members' claims. Demo tenants hold synthetic data,
/// so by default they may browse.
/// </summary>
public enum SearchCriteriaMode
{
    /// <summary>Require criteria unless the current tenant is a demo tenant (default).</summary>
    NonDemoTenants,

    /// <summary>Always require criteria, demo tenants included.</summary>
    Always,

    /// <summary>Never require criteria. Only for deployments with synthetic data.</summary>
    Never
}

public interface ISearchCriteriaPolicy
{
    /// <summary>True when the current user must enter a criterion before searching.</summary>
    Task<bool> RequiresCriteriaAsync();
}

/// <summary>
/// Reads <c>Portal:Search:RequireCriteria</c> (NonDemoTenants | Always | Never).
/// Fails closed: an unreadable setting or an unknown tenant requires criteria.
/// </summary>
public sealed class SearchCriteriaPolicy : ISearchCriteriaPolicy
{
    public const string ConfigKey = "Portal:Search:RequireCriteria";

    private readonly IConfiguration _configuration;
    private readonly ITenantContextService _tenantContext;
    private readonly ILogger<SearchCriteriaPolicy> _logger;

    public SearchCriteriaPolicy(
        IConfiguration configuration,
        ITenantContextService tenantContext,
        ILogger<SearchCriteriaPolicy> logger)
    {
        _configuration = configuration;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public static SearchCriteriaMode ParseMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? SearchCriteriaMode.NonDemoTenants
            : Enum.TryParse<SearchCriteriaMode>(value.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                ? mode
                : SearchCriteriaMode.Always;

    public static bool Requires(SearchCriteriaMode mode, bool? isDemoTenant) => mode switch
    {
        SearchCriteriaMode.Never => false,
        SearchCriteriaMode.NonDemoTenants => isDemoTenant != true,
        _ => true
    };

    public async Task<bool> RequiresCriteriaAsync()
    {
        var mode = ParseMode(_configuration[ConfigKey]);
        if (mode != SearchCriteriaMode.NonDemoTenants)
        {
            return Requires(mode, null);
        }

        try
        {
            var context = await _tenantContext.GetCurrentTenantContextAsync();
            return Requires(mode, context?.IsDemo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant context unavailable; requiring search criteria");
            return true;
        }
    }
}
