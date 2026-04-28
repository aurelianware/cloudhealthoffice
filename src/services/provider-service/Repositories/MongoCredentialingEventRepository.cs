using System.Text;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Mongo-backed reader for the credentialing event stream. Append happens
/// in <see cref="Services.MongoCredentialingEventPublisher"/>; this class
/// is read-only by contract.
/// </summary>
public sealed class MongoCredentialingEventRepository : ICredentialingEventRepository
{
    private readonly IMongoCollection<CredentialingEvent> _collection;

    public MongoCredentialingEventRepository(
        IMongoDatabase database,
        IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:CredentialingEventsContainer"]
            ?? "CredentialingEvents";
        _collection = database.GetCollection<CredentialingEvent>(collectionName);
    }

    public async Task<IReadOnlyList<CredentialingEvent>> ListAscendingAsync(
        string tenantId, string providerId, CancellationToken ct = default)
    {
        var b = Builders<CredentialingEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId));
        return await _collection.Find(filter)
            .SortBy(x => x.Version)
            .ToListAsync(ct);
    }

    public async Task<CredentialingEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct = default)
    {
        var b = Builders<CredentialingEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId),
            b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
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

        var b = Builders<CredentialingEvent>.Filter;
        var filters = new List<FilterDefinition<CredentialingEvent>>
        {
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId),
        };
        if (afterVersion.HasValue)
        {
            // Newest-first cursor: return items strictly older than the
            // last version surfaced on the previous page.
            filters.Add(b.Lt(x => x.Version, afterVersion.Value));
        }

        // Fetch one extra row to detect whether more pages exist without
        // a separate count query.
        var page = await _collection.Find(b.And(filters))
            .SortByDescending(x => x.Version)
            .Limit(safeLimit + 1)
            .ToListAsync(ct);

        string? next = null;
        if (page.Count > safeLimit)
        {
            page.RemoveAt(page.Count - 1);
            next = EncodeCursor(page[^1].Version);
        }

        return new CredentialingHistoryPage(page, next);
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
