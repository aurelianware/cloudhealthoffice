using System.Net.Http.Json;
using System.Net;
using MongoDB.Driver;
using MongoDB.Bson;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Subscription records. Writes (and the PlatformTenants listing) go to
/// tenant-service's platform API (<c>/v1/platform/subscriptions</c>,
/// platform:tenants) with the user's CHO token; tenant-service is the only
/// writer of the <c>Tenants</c> collection. Self-service signup goes through
/// the token service (<see cref="IChoTokenProvider.SignupAsync"/>). The
/// sign-in reads below still read the collection directly.
/// </summary>
public class TenantService : ITenantService
{
    private readonly IMongoCollection<TenantSubscription> _tenantsCollection;
    private readonly IMongoCollection<BsonDocument> _membersCollection;
    private readonly IMongoCollection<BsonDocument> _tenantUsersCollection;
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantService> _logger;

    public TenantService(IMongoClient mongoClient, HttpClient http, IConfiguration configuration, ILogger<TenantService> logger)
    {
        _logger = logger;
        _http = http;
        _configuration = configuration;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _tenantsCollection = db.GetCollection<TenantSubscription>(
            configuration["MongoDB:TenantsCollection"] ?? "Tenants");
        _tenantUsersCollection = db.GetCollection<BsonDocument>("TenantUsers");
        _membersCollection = db.GetCollection<BsonDocument>(
            configuration["MongoDB:MembersCollection"] ?? "Members");
    }

    public async Task<TenantSubscription?> GetSubscriptionByAzureTenantIdAsync(string azureTenantId)
    {
        _logger.LogInformation("Looking up subscription for Azure Tenant ID: {TenantId}", azureTenantId);

        if (string.IsNullOrEmpty(azureTenantId) || azureTenantId == "common")
            return null;

        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var tenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

        if (tenant != null)
            _logger.LogInformation("Found subscription for tenant {TenantId}: {OrgName} ({Status})",
                azureTenantId, tenant.OrganizationName, tenant.SubscriptionStatus);
        else
            _logger.LogInformation("No subscription found for Azure Tenant ID: {TenantId}", azureTenantId);

        return tenant;
    }

    public async Task<TenantSubscription?> GetDemoTenantAsync()
    {
        try
        {
            _logger.LogInformation("Fetching demo tenant");

            var filter = Builders<TenantSubscription>.Filter.Eq(t => t.IsDemo, true);
            var demoTenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

            if (demoTenant != null)
                return demoTenant;

            _logger.LogWarning("No demo tenant found in MongoDB, returning default");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching demo tenant from MongoDB");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
    }

    public async Task<bool> IsMemberOfTenantAsync(string azureTenantId, string userEmail)
    {
        try
        {
            _logger.LogInformation("Checking if {Email} is member of tenant {TenantId}", userEmail, azureTenantId);

            if (string.IsNullOrEmpty(userEmail) || string.IsNullOrEmpty(azureTenantId))
                return false;

            var tenant = await GetSubscriptionByAzureTenantIdAsync(azureTenantId);
            if (tenant == null)
            {
                _logger.LogWarning("Tenant not found for Azure Tenant ID: {TenantId}", azureTenantId);
                return false;
            }

            if (tenant.AdminEmails.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("User {Email} is admin for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check TenantUsers collection (RBAC users managed by tenant-service)
            var tenantUserFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("emailNormalized", userEmail.ToLowerInvariant()),
                Builders<BsonDocument>.Filter.Eq("status", "Active"));
            var tenantUserCount = await _tenantUsersCollection.CountDocumentsAsync(tenantUserFilter);
            if (tenantUserCount > 0)
            {
                _logger.LogInformation("User {Email} found in TenantUsers for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check Members collection (legacy member records)
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("email", userEmail.ToLowerInvariant()));
            var count = await _membersCollection.CountDocumentsAsync(filter);
            var hasMember = count > 0;

            _logger.LogInformation("User {Email} member status for tenant {TenantId}: {IsMember}",
                userEmail, azureTenantId, hasMember);
            return hasMember;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking membership for {Email} in tenant {TenantId}", userEmail, azureTenantId);
            return false;
        }
    }

    private string PlatformSubscriptionsUrl =>
        $"{(_configuration["Services:TenantService"] ?? "http://tenant-service.cloudhealthoffice/api").TrimEnd('/')}/v1/platform/subscriptions";

    private string SubscriptionUrl(string azureTenantId) => $"{PlatformSubscriptionsUrl}/{Uri.EscapeDataString(azureTenantId)}";

    private async Task<HttpResponseMessage> SendPlatformAsync(HttpMethod method, string url, object? body, string what)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Tenant Service");
            throw new ServiceUnavailableException("Tenant Service", ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new KeyNotFoundException($"No subscription found ({what}).");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            _logger.LogWarning("Tenant service refused {What}: {Status}", what, status);
            throw new InvalidOperationException($"tenant-service refused {what} ({status}).");
        }
        return response;
    }

    /// <summary>A subscription created by a platform administrator (PlatformTenants): tenant-service, platform:tenants.</summary>
    public async Task<string> CreateTenantAsync(CreateTenantRequest request)
    {
        // Merge AdminEmail into AdminEmails list
        var adminEmails = request.AdminEmails ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(request.AdminEmail) && !adminEmails.Contains(request.AdminEmail))
            adminEmails.Add(request.AdminEmail);

        using var response = await SendPlatformAsync(HttpMethod.Post, PlatformSubscriptionsUrl, new
        {
            azureTenantId = request.AzureTenantId,
            organizationName = request.OrganizationName,
            subscriptionStatus = request.SubscriptionStatus,
            tier = request.Tier,
            isDemo = request.IsDemo,
            adminEmails,
            notes = request.Notes,
            stripeCustomerId = request.StripeCustomerId,
            stripeSubscriptionId = request.StripeSubscriptionId,
        }, "the subscription create");

        var created = await response.Content.ReadFromJsonAsync<TenantSubscription>();
        if (string.IsNullOrEmpty(created?.TenantId))
            throw new InvalidOperationException("tenant-service returned no tenant id.");
        _logger.LogInformation("Created tenant {TenantId} for organization {OrgName}", created.TenantId, request.OrganizationName);
        return created.TenantId;
    }

    public async Task UpdateTenantAsync(string azureTenantId, UpdateTenantRequest request)
    {
        using var _ = await SendPlatformAsync(HttpMethod.Put, SubscriptionUrl(azureTenantId), new
        {
            organizationName = request.OrganizationName,
            tier = request.Tier,
            subscriptionStatus = request.SubscriptionStatus,
            adminEmails = request.AdminEmails,
            isDemo = request.IsDemo,
            notes = request.Notes,
        }, "the subscription update");
        _logger.LogInformation("Updated tenant {AzureTenantId}: {OrgName}", azureTenantId, request.OrganizationName);
    }

    public async Task DeleteTenantAsync(string azureTenantId)
    {
        using var _ = await SendPlatformAsync(HttpMethod.Delete, SubscriptionUrl(azureTenantId), null, "the subscription delete");
        _logger.LogInformation("Deleted tenant {AzureTenantId}", azureTenantId);
    }

    public async Task<List<TenantSubscription>> GetAllSubscriptionsAsync()
    {
        using var response = await SendPlatformAsync(HttpMethod.Get, PlatformSubscriptionsUrl, null, "the subscription list");
        return await response.Content.ReadFromJsonAsync<List<TenantSubscription>>() ?? new List<TenantSubscription>();
    }

    public async Task UpdateSubscriptionStatusAsync(string azureTenantId, string status)
    {
        using var _ = await SendPlatformAsync(HttpMethod.Put, $"{SubscriptionUrl(azureTenantId)}/status", new { status },
            "the subscription status change");
        _logger.LogInformation("Updated subscription status for tenant {TenantId} to {Status}", azureTenantId, status);
    }

    public async Task<List<TenantSubscription>> GetTenantsForUserAsync(string userEmail)
    {
        if (string.IsNullOrEmpty(userEmail))
            return new List<TenantSubscription>();

        try
        {
            _logger.LogInformation("Finding tenants for user {Email}", userEmail);
            var emailLower = userEmail.ToLowerInvariant();

            // Find all tenants where the user's email appears in adminEmails
            var adminFilter = Builders<TenantSubscription>.Filter.AnyIn(
                t => t.AdminEmails, new[] { userEmail, emailLower });
            var tenantsByAdmin = await _tenantsCollection.Find(adminFilter).ToListAsync();

            // Also check TenantUsers collection for tenants the user has a role in
            var tenantUserFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("emailNormalized", emailLower),
                Builders<BsonDocument>.Filter.Eq("status", "Active"));
            var tenantUsers = await _tenantUsersCollection.Find(tenantUserFilter).ToListAsync();

            var additionalTenantIds = tenantUsers
                .Select(tu => tu.GetValue("tenantId", "").AsString)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            // Fetch any additional tenants found via TenantUsers that weren't in admin results
            var existingTenantIds = tenantsByAdmin.Select(t => t.TenantId).ToHashSet();
            var missingTenantIds = additionalTenantIds.Except(existingTenantIds).ToList();

            if (missingTenantIds.Count > 0)
            {
                var tenantIdFilter = Builders<TenantSubscription>.Filter.In(t => t.TenantId, missingTenantIds);
                var additionalTenants = await _tenantsCollection.Find(tenantIdFilter).ToListAsync();
                tenantsByAdmin.AddRange(additionalTenants);
            }

            _logger.LogInformation("Found {Count} tenants for user {Email}", tenantsByAdmin.Count, userEmail);
            return tenantsByAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding tenants for user {Email}", userEmail);
            return new List<TenantSubscription>();
        }
    }
}
