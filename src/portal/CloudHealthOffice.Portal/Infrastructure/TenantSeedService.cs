using CloudHealthOffice.Portal.Services;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Seeds the MongoDB tenant subscription on startup as a background task.
/// Runs after app.Run() so health probes can respond immediately.
/// </summary>
public class TenantSeedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantSeedService> _logger;

    public TenantSeedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TenantSeedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short delay to let the app finish starting up
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // Timeout: don't hang forever

        try
        {
            var mongoClient = _serviceProvider.GetRequiredService<IMongoClient>();
            var dbName = _configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
            var collectionName = _configuration["MongoDB:TenantsCollection"] ?? "Tenants";
            var db = mongoClient.GetDatabase(dbName);
            var collection = db.GetCollection<TenantSubscription>(collectionName);

            var seedAzureTenantId = _configuration["SeedTenant:AzureTenantId"]
                                    ?? "32177734-051b-4fdc-9568-cc35530191b1";
            var seedTenantId = _configuration["SeedTenant:TenantId"] ?? "aurelianware";
            var seedOrgName = _configuration["SeedTenant:OrganizationName"] ?? "Cloud Health Office";
            var seedAdminEmail = _configuration["SeedTenant:AdminEmail"] ?? "";
            var seedTier = _configuration["SeedTenant:Tier"] ?? "professional";

            var existing = await collection.Find(
                    Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, seedAzureTenantId))
                .FirstOrDefaultAsync(cts.Token);

            if (existing == null)
            {
                var adminEmails = string.IsNullOrEmpty(seedAdminEmail)
                    ? new List<string>()
                    : new List<string> { seedAdminEmail };

                var subscription = new TenantSubscription
                {
                    TenantId = seedTenantId,
                    AzureTenantId = seedAzureTenantId,
                    OrganizationName = seedOrgName,
                    SubscriptionStatus = "Active",
                    Tier = seedTier,
                    IsDemo = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    AdminEmails = adminEmails
                };
                await collection.InsertOneAsync(subscription, cancellationToken: cts.Token);
                _logger.LogInformation(
                    "Seeded tenant subscription for Azure Tenant {TenantId} ({OrgName})",
                    seedAzureTenantId, seedOrgName);
            }
            else
            {
                _logger.LogDebug("Tenant subscription already exists for Azure Tenant {TenantId}",
                    seedAzureTenantId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down, ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to auto-seed tenant subscription. User may be redirected to /signup until a subscription is created.");
        }
    }
}
