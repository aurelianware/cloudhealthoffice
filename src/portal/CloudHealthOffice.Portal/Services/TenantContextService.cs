using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The CHO tenant the signed-in user is acting in, for display and for page code
/// that needs the tenant id. The tenant, and which tenants the user may use, come
/// from the CHO token service (via <see cref="IChoTokenProvider"/>); this service
/// makes no access decisions of its own. Subscription details (tier, demo flag)
/// are looked up for display only.
///
/// It depends on AuthenticationStateProvider, which is only valid inside a Blazor
/// component DI scope, so call it from components or circuit-scoped services.
/// </summary>
public interface ITenantContextService
{
    Task<TenantContext?> GetCurrentTenantContextAsync();
    Task<string?> GetTenantIdAsync();
    string? TenantId { get; }
    string? TenantName { get; }
    bool IsDemo { get; }

    /// <summary>The tenants the CHO token service lists for the current user.</summary>
    Task<List<TenantSubscription>> GetAvailableTenantsAsync();

    /// <summary>
    /// Switch to another CHO tenant (by CHO tenant id). Only tenants the token
    /// service listed are attempted, and the token service decides the exchange.
    /// </summary>
    Task<bool> SwitchTenantAsync(string tenantId);

    /// <summary>
    /// Display only: a platform administrator is acting in a tenant outside their
    /// own Entra organisation.
    /// </summary>
    bool IsImpersonating { get; }
}

public class TenantContextService : ITenantContextService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IChoTokenProvider _tokenProvider;
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantContextService> _logger;
    private readonly IConfiguration _configuration;
    private TenantContext? _cachedContext;
    private List<TenantSubscription>? _cachedAvailableTenants;
    private bool _isImpersonating;

    public string? TenantId => _cachedContext?.TenantId;
    public string? TenantName => _cachedContext?.TenantName;
    public bool IsDemo => _cachedContext?.IsDemo ?? false;
    public bool IsImpersonating => _isImpersonating;

    public TenantContextService(
        AuthenticationStateProvider authenticationStateProvider,
        IChoTokenProvider tokenProvider,
        ITenantService tenantService,
        ILogger<TenantContextService> logger,
        IConfiguration configuration)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _tokenProvider = tokenProvider;
        _tenantService = tenantService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<TenantContext?> GetCurrentTenantContextAsync()
    {
        if (_cachedContext != null)
            return _cachedContext;

        var user = await GetUserAsync();
        if (user == null)
        {
            _logger.LogDebug("User not authenticated, no tenant context available");
            return null;
        }

        var result = await _tokenProvider.GetTokenAsync();
        if (!result.Succeeded)
        {
            // No CHO token means no tenant: the user has access to nothing.
            _logger.LogWarning("No CHO tenant for the signed-in user ({Status})", result.Status);
            return null;
        }

        _cachedContext = await BuildTenantContextAsync(user, result.Token!);
        _logger.LogInformation("Tenant context resolved: {TenantName} ({TenantId})",
            _cachedContext.TenantName, _cachedContext.TenantId);
        return _cachedContext;
    }

    public async Task<string?> GetTenantIdAsync()
    {
        var context = await GetCurrentTenantContextAsync();
        return context?.TenantId;
    }

    public async Task<List<TenantSubscription>> GetAvailableTenantsAsync()
    {
        if (_cachedAvailableTenants != null)
            return _cachedAvailableTenants;

        var tenants = await _tokenProvider.GetTenantsAsync();
        if (tenants == null)
            return new List<TenantSubscription>();

        var list = new List<TenantSubscription>();
        foreach (var tenant in tenants)
        {
            var subscription = await FindSubscriptionAsync(tenant.AzureTenantId, tenant.TenantId);
            list.Add(new TenantSubscription
            {
                TenantId = tenant.TenantId,
                AzureTenantId = tenant.AzureTenantId ?? string.Empty,
                OrganizationName = FirstNonEmpty(tenant.TenantName, subscription?.OrganizationName) ?? tenant.TenantId,
                Tier = subscription?.Tier ?? string.Empty,
                SubscriptionStatus = subscription?.SubscriptionStatus ?? string.Empty,
                IsDemo = subscription?.IsDemo ?? IsLocalDemoMode(),
            });
        }

        _cachedAvailableTenants = list;
        return _cachedAvailableTenants;
    }

    public async Task<bool> SwitchTenantAsync(string tenantId)
    {
        try
        {
            var user = await GetUserAsync();
            if (user == null)
                return false;

            var result = await _tokenProvider.SwitchTenantAsync(tenantId);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Switch to tenant {TenantId} refused ({Status})", tenantId, result.Status);
                return false;
            }

            _cachedContext = await BuildTenantContextAsync(user, result.Token!);
            _logger.LogInformation("Switched tenant context to: {TenantName} ({TenantId})",
                _cachedContext.TenantName, _cachedContext.TenantId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching to tenant {TenantId}", tenantId);
            return false;
        }
    }

    private async Task<TenantContext> BuildTenantContextAsync(ClaimsPrincipal user, ChoTokenExchangeResponse token)
    {
        var userEmail = FirstNonEmpty(
            token.User?.Email,
            user.FindFirst(ClaimTypes.Email)?.Value,
            user.FindFirst("preferred_username")?.Value,
            user.FindFirst("upn")?.Value);

        if (IsLocalDemoMode() && user.HasClaim("cho_local_demo", "true"))
        {
            _isImpersonating = false;
            return new TenantContext
            {
                TenantId = token.TenantId,
                TenantName = FirstNonEmpty(token.TenantName, _configuration["Authentication:LocalDemo:TenantName"]) ?? "Local Demo Tenant",
                AzureTenantId = _configuration["Authentication:LocalDemo:AzureTenantId"]
                                ?? user.FindFirst("tid")?.Value ?? "local-demo",
                SubscriptionTier = "local-demo",
                SubscriptionStatus = "Active",
                IsDemo = true,
                UserEmail = userEmail
            };
        }

        var homeAzureTenantId = HomeAzureTenantId(user);
        var listed = await FindListedTenantAsync(token.TenantId);
        // The tenant list names the Entra organisation of each CHO tenant; without
        // it, try the user's own organisation (accepted only if it is this tenant).
        var subscription = await FindSubscriptionAsync(
            FirstNonEmpty(listed?.AzureTenantId, homeAzureTenantId), token.TenantId);
        var azureTenantId = FirstNonEmpty(listed?.AzureTenantId, subscription?.AzureTenantId);

        _isImpersonating = !string.IsNullOrEmpty(azureTenantId)
                           && !string.Equals(azureTenantId, homeAzureTenantId, StringComparison.OrdinalIgnoreCase)
                           && token.Permissions.Contains("platform:admin", StringComparer.OrdinalIgnoreCase);

        return new TenantContext
        {
            TenantId = token.TenantId,
            TenantName = FirstNonEmpty(token.TenantName, listed?.TenantName, subscription?.OrganizationName) ?? token.TenantId,
            AzureTenantId = azureTenantId ?? string.Empty,
            SubscriptionTier = subscription?.Tier ?? string.Empty,
            SubscriptionStatus = subscription?.SubscriptionStatus ?? string.Empty,
            IsDemo = subscription?.IsDemo ?? false,
            UserEmail = userEmail
        };
    }

    private async Task<ChoTenantInfo?> FindListedTenantAsync(string tenantId)
    {
        try
        {
            var tenants = await _tokenProvider.GetTenantsAsync();
            return tenants?.FirstOrDefault(t => string.Equals(t.TenantId, tenantId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenant list unavailable for display");
            return null;
        }
    }

    /// <summary>Subscription details for display (tier, demo flag). Never used to grant access.</summary>
    private async Task<TenantSubscription?> FindSubscriptionAsync(string? azureTenantId, string tenantId)
    {
        if (string.IsNullOrEmpty(azureTenantId) || IsLocalDemoMode())
            return null;

        try
        {
            var subscription = await _tenantService.GetSubscriptionByAzureTenantIdAsync(azureTenantId);
            // Only describe the tenant the token names, never a different one.
            return subscription != null && string.Equals(subscription.TenantId, tenantId, StringComparison.Ordinal)
                ? subscription
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Subscription details unavailable for tenant {TenantId}", tenantId);
            return null;
        }
    }

    private async Task<ClaimsPrincipal?> GetUserAsync()
    {
        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return authState.User.Identity?.IsAuthenticated == true ? authState.User : null;
    }

    private static string? HomeAzureTenantId(ClaimsPrincipal user)
        => user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
           ?? user.FindFirst("tid")?.Value;

    private bool IsLocalDemoMode()
        => string.Equals(_configuration["Authentication:Mode"], "LocalDemo", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public class TenantContext
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string SubscriptionTier { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty;
    public bool IsDemo { get; set; }
    public string? UserEmail { get; set; }
}
