namespace CloudHealthOffice.Portal.Services;

public interface ITenantService
{
    Task<TenantSubscription?> GetSubscriptionByAzureTenantIdAsync(string azureTenantId);
    Task<TenantSubscription?> GetDemoTenantAsync();
    Task<bool> IsMemberOfTenantAsync(string azureTenantId, string userEmail);
    Task<string> CreateTenantAsync(CreateTenantRequest request);
    Task UpdateTenantAsync(string azureTenantId, UpdateTenantRequest request);
    Task DeleteTenantAsync(string azureTenantId);
    Task<List<TenantSubscription>> GetAllSubscriptionsAsync();
    Task UpdateSubscriptionStatusAsync(string azureTenantId, string status);
    /// <summary>
    /// Get all tenant subscriptions where the given email appears in admin emails
    /// or user roster. Used for tenant switcher when home tenant ID doesn't match.
    /// </summary>
    Task<List<TenantSubscription>> GetTenantsForUserAsync(string userEmail);
}
