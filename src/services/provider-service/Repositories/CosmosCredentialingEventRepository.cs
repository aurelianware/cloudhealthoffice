using System.Text;
using Microsoft.Azure.Cosmos;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Cosmos-backed reader for the credentialing event stream. Used in
/// Cosmos-only deployments that don't yet provision the events stream
/// (paired with <see cref="Services.NoopCredentialingEventPublisher"/>).
/// All reads are partition-scoped by <c>TenantId</c>.
/// </summary>
public sealed class CosmosCredentialingEventRepository : ICredentialingEventRepository
{
    private readonly Container _container;

    public CosmosCredentialingEventRepository(
        CosmosClient client,
        IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:CredentialingEventsContainer"]
            ?? "CredentialingEvents";
        _container = client.GetContainer(databaseName, containerName);
    }

    public async Task<IReadOnlyList<CredentialingEvent>> ListAscendingAsync(
        string tenantId, string providerId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerId = @providerId " +
                "ORDER BY c.version ASC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId);

        return await ReadAllAsync(query, tenantId, ct);
    }

    public async Task<CredentialingEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId AND c.providerId = @providerId AND c.eventId = @eventId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@eventId", eventId);

        var rows = await ReadAllAsync(query, tenantId, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<CredentialingHistoryPage> ListHistoryDescendingAsync(
        string tenantId,
        string providerId,
        string? continuationToken,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var afterVersion = DecodeCursor(continuationToken);

        var sql = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerId = @providerId";
        if (afterVersion.HasValue)
        {
            sql += " AND c.version < @afterVersion";
        }
        sql += $" ORDER BY c.version DESC OFFSET 0 LIMIT {safeLimit + 1}";

        var query = new QueryDefinition(sql)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId);
        if (afterVersion.HasValue)
        {
            query = query.WithParameter("@afterVersion", afterVersion.Value);
        }

        var page = await ReadAllAsync(query, tenantId, ct);
        string? next = null;
        if (page.Count > safeLimit)
        {
            page = page.Take(safeLimit).ToList();
            next = EncodeCursor(page[^1].Version);
        }
        return new CredentialingHistoryPage(page, next);
    }

    private async Task<List<CredentialingEvent>> ReadAllAsync(QueryDefinition query, string tenantId, CancellationToken ct)
    {
        var iterator = _container.GetItemQueryIterator<CredentialingEvent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<CredentialingEvent>();
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    private static int? DecodeCursor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var bytes = Convert.FromBase64String(token);
            var decoded = Encoding.UTF8.GetString(bytes);
            return int.TryParse(decoded, out var v) ? v : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EncodeCursor(int lastVersion)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(lastVersion.ToString()));
}
