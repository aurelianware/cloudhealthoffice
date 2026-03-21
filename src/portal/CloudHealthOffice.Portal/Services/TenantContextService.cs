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
}

public class TenantContextService : ITenantContextService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantContextService> _logger;
    private TenantContext? _cachedContext;

    public string? TenantId => _cachedContext?.TenantId;
    public string? TenantName => _cachedContext?.TenantName;
    public bool IsDemo => _cachedContext?.IsDemo ?? false;

    public TenantContextService(
        AuthenticationStateProvider authenticationStateProvider,
        ITenantService tenantService,
        ILogger<TenantContextService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _tenantService = tenantService;
        _logger = logger;
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

        try
        {
            // Query subscription by Azure Tenant ID
            var subscription = await _tenantService.GetSubscriptionByAzureTenantIdAsync(azureTenantId);

            if (subscription == null)
            {
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

            _cachedContext = new TenantContext
            {
                TenantId = subscription.TenantId ?? azureTenantId,
                TenantName = subscription.OrganizationName ?? "Unknown Tenant",
                AzureTenantId = azureTenantId,
                SubscriptionTier = subscription.Tier ?? "starter",
                SubscriptionStatus = subscription.SubscriptionStatus ?? "Unknown",
                IsDemo = subscription.IsDemo,
                UserEmail = userEmail
            };

            _logger.LogInformation("Tenant context resolved: {TenantName} ({TenantId})", 
                _cachedContext.TenantName, _cachedContext.TenantId);

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
