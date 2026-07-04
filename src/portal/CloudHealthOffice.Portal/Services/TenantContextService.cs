using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Service to manage tenant context for the current authenticated user.
/// Extracts tenant information from Azure AD claims and subscription data.
///
/// IMPORTANT: This service depends on AuthenticationStateProvider, which is only
/// valid inside a Blazor component DI scope. Do NOT call GetCurrentTenantContextAsync
/// or GetTenantIdAsync from DelegatingHandlers or other infrastructure that resolves
/// outside the Razor circuit scope. Instead, have Razor components (e.g. MainLayout)
/// pre-resolve the tenant context and propagate the tenant ID via
/// HttpClient.DefaultRequestHeaders["X-Tenant-ID"].
/// </summary>
public interface ITenantContextService
{
    Task<TenantContext?> GetCurrentTenantContextAsync();
    Task<string?> GetTenantIdAsync();
    string? TenantId { get; }
    string? TenantName { get; }
    bool IsDemo { get; }

    /// <summary>
    /// Get all tenants the current user has access to (by Azure AD membership,
    /// guest access, or admin email association)
    /// </summary>
    Task<List<TenantSubscription>> GetAvailableTenantsAsync();

    /// <summary>
    /// Switch the current session to a different tenant. The user must have
    /// access to the target tenant (verified by GetAvailableTenantsAsync).
    /// </summary>
    Task<bool> SwitchTenantAsync(string azureTenantId);

    /// <summary>
    /// Whether the current user is impersonating a tenant they don't directly belong to.
    /// Only applicable for platform admins.
    /// </summary>
    bool IsImpersonating { get; }
}

public class TenantContextService : ITenantContextService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantContextService> _logger;
    private readonly IConfiguration _configuration;
    private TenantContext? _cachedContext;
    private List<TenantSubscription>? _cachedAvailableTenants;
    private string? _homeTenantId; // Original Azure AD tenant ID from claims — never changes after first resolution
    private bool _isImpersonating;

    public string? TenantId => _cachedContext?.TenantId;
    public string? TenantName => _cachedContext?.TenantName;
    public bool IsDemo => _cachedContext?.IsDemo ?? false;
    public bool IsImpersonating => _isImpersonating;

    public TenantContextService(
        AuthenticationStateProvider authenticationStateProvider,
        ITenantService tenantService,
        ILogger<TenantContextService> logger,
        IConfiguration configuration)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _tenantService = tenantService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<TenantContext?> GetCurrentTenantContextAsync()
    {
        // Return cached context if available
        if (_cachedContext != null)
        {
            return _cachedContext;
        }

        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            _logger.LogDebug("User not authenticated, no tenant context available");
            return null;
        }

        if (IsLocalDemoUser(user))
        {
            _cachedContext = BuildLocalDemoTenantContext(user);
            _logger.LogInformation("Tenant context resolved via local demo auth: {TenantName} ({TenantId})",
                _cachedContext.TenantName, _cachedContext.TenantId);
            return _cachedContext;
        }

        // Extract Azure AD Tenant ID from claims
        var azureTenantId = user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                         ?? user.FindFirst("tid")?.Value;

        var userEmail = user.FindFirst(ClaimTypes.Email)?.Value
                     ?? user.FindFirst("preferred_username")?.Value
                     ?? user.FindFirst("upn")?.Value;

        if (string.IsNullOrEmpty(azureTenantId) || azureTenantId == "common")
        {
            _logger.LogWarning("Unable to extract tenant ID from user claims");
            return null;
        }

        // Cache the original home tenant ID from claims — this never changes,
        // even after SwitchTenantAsync updates _cachedContext.AzureTenantId.
        _homeTenantId ??= azureTenantId;

        try
        {
            // Step 1: Query subscription by Azure Tenant ID (home tenant match)
            var subscription = await _tenantService.GetSubscriptionByAzureTenantIdAsync(azureTenantId);

            if (subscription != null)
            {
                _cachedContext = BuildTenantContext(subscription, azureTenantId, userEmail);
                _logger.LogInformation("Tenant context resolved via home tenant: {TenantName} ({TenantId})",
                    _cachedContext.TenantName, _cachedContext.TenantId);
                return _cachedContext;
            }

            // Step 2: Home tenant didn't match — guest user scenario
            // Check if user's email appears in any tenant's admin emails or user list
            if (!string.IsNullOrEmpty(userEmail))
            {
                _logger.LogInformation(
                    "No subscription for home tenant {HomeTenantId}, checking email-based tenant resolution for {Email}",
                    azureTenantId, userEmail);

                var userTenants = await _tenantService.GetTenantsForUserAsync(userEmail);

                if (userTenants.Count == 1)
                {
                    // Exactly one match — auto-resolve
                    subscription = userTenants[0];
                    _cachedContext = BuildTenantContext(subscription, subscription.AzureTenantId, userEmail);
                    _logger.LogInformation(
                        "Guest user {Email} auto-resolved to tenant: {TenantName} ({TenantId})",
                        userEmail, _cachedContext.TenantName, _cachedContext.TenantId);
                    return _cachedContext;
                }

                if (userTenants.Count > 1)
                {
                    // Multiple matches — cache the list, default to first
                    _cachedAvailableTenants = userTenants;
                    subscription = userTenants[0];
                    _cachedContext = BuildTenantContext(subscription, subscription.AzureTenantId, userEmail);
                    _logger.LogInformation(
                        "Guest user {Email} has access to {Count} tenants, defaulting to: {TenantName}",
                        userEmail, userTenants.Count, _cachedContext.TenantName);
                    return _cachedContext;
                }
            }

            _logger.LogWarning("No subscription found for Azure Tenant ID: {TenantId}, using default tenant context", azureTenantId);
            // Fallback: use the Azure AD tenant ID directly so the portal
            // remains functional before a subscription is formally created
            _cachedContext = new TenantContext
            {
                TenantId = azureTenantId,
                TenantName = userEmail?.Split('@').LastOrDefault() ?? "Cloud Health Office",
                AzureTenantId = azureTenantId,
                SubscriptionTier = "professional",
                SubscriptionStatus = "Active",
                IsDemo = false,
                UserEmail = userEmail
            };
            return _cachedContext;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving tenant context for Azure Tenant ID: {TenantId}, using default", azureTenantId);
            _cachedContext = new TenantContext
            {
                TenantId = azureTenantId,
                TenantName = userEmail?.Split('@').LastOrDefault() ?? "Cloud Health Office",
                AzureTenantId = azureTenantId,
                SubscriptionTier = "professional",
                SubscriptionStatus = "Active",
                IsDemo = false,
                UserEmail = userEmail
            };
            return _cachedContext;
        }
    }

    public async Task<string?> GetTenantIdAsync()
    {
        var context = await GetCurrentTenantContextAsync();
        return context?.TenantId;
    }

    public async Task<List<TenantSubscription>> GetAvailableTenantsAsync()
    {
        // Return cached list if available
        if (_cachedAvailableTenants != null)
            return _cachedAvailableTenants;

        // Ensure current context is resolved first
        var currentContext = await GetCurrentTenantContextAsync();
        if (currentContext == null)
            return new List<TenantSubscription>();

        if (currentContext.IsDemo && string.Equals(
                _configuration["Authentication:Mode"],
                "LocalDemo",
                StringComparison.OrdinalIgnoreCase))
        {
            _cachedAvailableTenants = new List<TenantSubscription>
            {
                new()
                {
                    TenantId = currentContext.TenantId,
                    AzureTenantId = currentContext.AzureTenantId,
                    OrganizationName = currentContext.TenantName,
                    SubscriptionStatus = currentContext.SubscriptionStatus,
                    Tier = currentContext.SubscriptionTier,
                    IsDemo = true,
                    AdminEmails = string.IsNullOrWhiteSpace(currentContext.UserEmail)
                        ? new List<string>()
                        : new List<string> { currentContext.UserEmail }
                }
            };
            return _cachedAvailableTenants;
        }

        var userEmail = currentContext.UserEmail;
        if (string.IsNullOrEmpty(userEmail))
            return new List<TenantSubscription>();

        try
        {
            // Get tenants the user has access to via email/admin association
            var userTenants = await _tenantService.GetTenantsForUserAsync(userEmail);

            // Also include the home tenant match if it exists and isn't already in the list.
            // Use _homeTenantId (cached from original claims) rather than currentContext.AzureTenantId,
            // which may reflect a switched-to tenant after SwitchTenantAsync.
            var homeTenantSubscription = _homeTenantId != null
                ? await _tenantService.GetSubscriptionByAzureTenantIdAsync(_homeTenantId)
                : null;
            if (homeTenantSubscription != null &&
                !userTenants.Any(t => t.AzureTenantId == homeTenantSubscription.AzureTenantId))
            {
                userTenants.Insert(0, homeTenantSubscription);
            }

            _cachedAvailableTenants = userTenants;
            return _cachedAvailableTenants;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available tenants for user {Email}", userEmail);
            return new List<TenantSubscription>();
        }
    }

    public async Task<bool> SwitchTenantAsync(string azureTenantId)
    {
        try
        {
            // Verify the user has access to this tenant
            var availableTenants = await GetAvailableTenantsAsync();
            var targetTenant = availableTenants.FirstOrDefault(t => t.AzureTenantId == azureTenantId);

            if (targetTenant == null)
            {
                // For platform admins, allow switching to any tenant (impersonation)
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;
                var isPlatformAdmin = user.IsInRole("PlatformAdmin") ||
                    user.Claims.Any(c => c.Type == "permissions" && c.Value.Contains("platform:admin"));

                if (isPlatformAdmin)
                {
                    targetTenant = await _tenantService.GetSubscriptionByAzureTenantIdAsync(azureTenantId);
                    if (targetTenant == null)
                    {
                        _logger.LogWarning("Platform admin attempted to switch to non-existent tenant {TenantId}", azureTenantId);
                        return false;
                    }
                    _isImpersonating = true;
                    _logger.LogWarning(
                        "Platform admin {Email} switched to tenant {OrgName} ({AzureTenantId})",
                        _cachedContext?.UserEmail, targetTenant.OrganizationName, azureTenantId);
                }
                else
                {
                    _logger.LogWarning("User attempted to switch to unauthorized tenant {TenantId}", azureTenantId);
                    return false;
                }
            }
            else
            {
                // Switching to an authorized tenant the user has explicit membership in
                // — this is NOT impersonation, even if it's not their home tenant
                _isImpersonating = false;
            }

            var userEmail = _cachedContext?.UserEmail;
            _cachedContext = BuildTenantContext(targetTenant, targetTenant.AzureTenantId, userEmail);

            _logger.LogInformation("Switched tenant context to: {TenantName} ({TenantId})",
                _cachedContext.TenantName, _cachedContext.TenantId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching to tenant {TenantId}", azureTenantId);
            return false;
        }
    }

    private static TenantContext BuildTenantContext(TenantSubscription subscription, string azureTenantId, string? userEmail)
    {
        return new TenantContext
        {
            TenantId = subscription.TenantId ?? azureTenantId,
            TenantName = subscription.OrganizationName ?? "Unknown Tenant",
            AzureTenantId = azureTenantId,
            SubscriptionTier = subscription.Tier ?? "starter",
            SubscriptionStatus = subscription.SubscriptionStatus ?? "Unknown",
            IsDemo = subscription.IsDemo,
            UserEmail = userEmail
        };
    }

    private bool IsLocalDemoUser(ClaimsPrincipal user)
        => string.Equals(
                _configuration["Authentication:Mode"],
                "LocalDemo",
                StringComparison.OrdinalIgnoreCase)
            && user.HasClaim("cho_local_demo", "true");

    private TenantContext BuildLocalDemoTenantContext(ClaimsPrincipal user)
    {
        var tenantId = _configuration["Authentication:LocalDemo:TenantId"]
            ?? user.FindFirst("extension_TenantId")?.Value
            ?? "demo";
        var azureTenantId = _configuration["Authentication:LocalDemo:AzureTenantId"]
            ?? user.FindFirst("tid")?.Value
            ?? "local-demo";
        var tenantName = _configuration["Authentication:LocalDemo:TenantName"]
            ?? "Local Demo Tenant";
        var email = user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("preferred_username")?.Value;

        return new TenantContext
        {
            TenantId = tenantId,
            TenantName = tenantName,
            AzureTenantId = azureTenantId,
            SubscriptionTier = "local-demo",
            SubscriptionStatus = "Active",
            IsDemo = true,
            UserEmail = email
        };
    }

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
