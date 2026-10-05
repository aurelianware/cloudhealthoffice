using Microsoft.Azure.Cosmos;
using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Append-only audit of claim-reservation releases and flags (who, which run,
/// which claim, which tenant, why). Tenant is explicit: the reconciliation job
/// writes it outside any request.
/// </summary>
public interface IReservationAuditLog
{
    Task RecordAsync(ReservationAuditEntry entry);

    /// <summary>A tenant's entries, newest first; one run's only when <paramref name="runId"/> is given.</summary>
    Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null);
}

public sealed class ReservationAuditLogMongo : IReservationAuditLog
{
    public const string CollectionName = "PaymentReservationAudit";

    private readonly IMongoCollection<ReservationAuditEntry> _collection;

    public ReservationAuditLogMongo(IMongoDatabase database)
        => _collection = database.GetCollection<ReservationAuditEntry>(CollectionName);

    public Task RecordAsync(ReservationAuditEntry entry) => _collection.InsertOneAsync(entry);

    public async Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null)
    {
        var filter = Builders<ReservationAuditEntry>.Filter.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrEmpty(runId))
            filter &= Builders<ReservationAuditEntry>.Filter.Eq(x => x.RunId, runId);
        return await _collection.Find(filter).SortByDescending(x => x.At).ToListAsync();
    }
}

/// <summary>Cosmos: container <c>PaymentReservationAudit</c>, partition key <c>/tenantId</c>, created if absent.</summary>
public sealed class ReservationAuditLogCosmos : IReservationAuditLog
{
    public const string ContainerName = "PaymentReservationAudit";

    private readonly Lazy<Task<Container>> _container;

    public ReservationAuditLogCosmos(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = new Lazy<Task<Container>>(async () =>
        {
            var response = await client.GetDatabase(databaseName)
                .CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/tenantId"));
            return response.Container;
        });
    }

    public async Task RecordAsync(ReservationAuditEntry entry)
    {
        var container = await _container.Value;
        await container.CreateItemAsync(entry, new PartitionKey(entry.TenantId));
    }

    public async Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null)
    {
        var container = await _container.Value;
        var query = new QueryDefinition(string.IsNullOrEmpty(runId)
                ? "SELECT * FROM c WHERE c.tenantId = @tenantId ORDER BY c.at DESC"
                : "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.runId = @runId ORDER BY c.at DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@runId", runId);
        var results = new List<ReservationAuditEntry>();
        var iterator = container.GetItemQueryIterator<ReservationAuditEntry>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }
}
