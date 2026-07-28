using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.HostedServices;

/// <summary>
/// Ensures provider and organization query indexes once at startup. Keeping
/// index administration out of scoped repository constructors prevents
/// transient Cosmos connection failures from failing ordinary API writes.
/// </summary>
public sealed class ProviderQueryIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<ProviderQueryIndexInitializer> _logger;

    public ProviderQueryIndexInitializer(
        IMongoDatabase database,
        ILogger<ProviderQueryIndexInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    internal static IReadOnlyList<CreateIndexModel<Provider>> BuildProviderIndexes()
    {
        var keys = Builders<Provider>.IndexKeys;
        return
        [
            new(keys.Ascending(p => p.TenantId).Ascending(p => p.NPI)),
            new(keys.Ascending(p => p.TenantId).Ascending(p => p.LastName)),
            new(keys.Ascending(p => p.TenantId).Ascending(p => p.OrganizationName)),
            new(keys.Ascending(p => p.TenantId).Ascending(p => p.ZipCode)),
            new(keys.Ascending(p => p.TenantId).Ascending("NetworkParticipations.PlanId")),
            new(keys.Ascending(p => p.TenantId).Ascending("NetworkParticipations.NetworkId")),
            new(keys.Ascending(p => p.TenantId)
                .Ascending("NetworkParticipations.NetworkId")
                .Ascending("NetworkParticipations.NetworkTier")),
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.ProviderId)
                .Ascending(p => p.VersionNumber)),
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.ProviderId)
                .Ascending(p => p.VersionId)),
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.ProviderId)
                .Ascending(p => p.VersionState)),
            // Cosmos DB for MongoDB requires composite indexes matching
            // multi-field sort shapes.
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.NPI)
                .Descending(p => p.VersionNumber)),
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.LastName)
                .Ascending(p => p.OrganizationName)
                .Ascending(p => p.Id)),
            new(keys.Ascending(p => p.TenantId)
                .Ascending(p => p.ProviderId)
                .Ascending(p => p.Id)),
            // Cosmos matches the ORDER BY document itself, not an
            // equality-filter prefix as MongoDB does.
            new(keys.Ascending(p => p.ProviderId).Ascending(p => p.Id)),
            new(keys.Ascending(p => p.LastName)
                .Ascending(p => p.OrganizationName)
                .Ascending(p => p.Id)),
            new(keys.Descending(p => p.VersionNumber))
        ];
    }

    internal static IReadOnlyList<CreateIndexModel<Organization>> BuildOrganizationIndexes()
    {
        var keys = Builders<Organization>.IndexKeys;
        return
        [
            new(keys.Ascending(o => o.TenantId)
                .Ascending(o => o.OrganizationId)
                .Ascending(o => o.VersionNumber)),
            new(keys.Ascending(o => o.TenantId)
                .Ascending(o => o.OrganizationId)
                .Ascending(o => o.VersionId)),
            new(keys.Ascending(o => o.TenantId).Ascending(o => o.NetworkType)),
            new(keys.Ascending(o => o.TenantId).Ascending(o => o.LineOfBusiness)),
            new(keys.Ascending(o => o.TenantId).Ascending(o => o.ParentOrganizationId)),
            new(keys.Ascending(o => o.TenantId).Ascending(o => o.VersionState)),
            new(keys.Ascending(o => o.TenantId).Ascending(o => o.Name)),
            new(keys.Descending(o => o.VersionNumber)),
            new(keys.Ascending(o => o.Name))
        ];
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.GetCollection<Provider>("Providers").Indexes
            .CreateManyAsync(BuildProviderIndexes(), cancellationToken);
        await _database.GetCollection<Organization>("Organizations").Indexes
            .CreateManyAsync(BuildOrganizationIndexes(), cancellationToken);

        _logger.LogInformation("Provider and organization query indexes ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
