using CloudHealthOffice.TradingPartnerService.Models;
using MongoDB.Driver;

namespace CloudHealthOffice.TradingPartnerService.Services;

public class TradingPartnerRepositoryMongo : ITradingPartnerRepository
{
    private readonly IMongoCollection<TradingPartner> _collection;
    private readonly ILogger<TradingPartnerRepositoryMongo> _logger;

    public TradingPartnerRepositoryMongo(
        IMongoDatabase database,
        ILogger<TradingPartnerRepositoryMongo> logger)
    {
        var collectionName = System.Environment.GetEnvironmentVariable("MONGO_COLLECTION_TRADING_PARTNERS")
            ?? System.Environment.GetEnvironmentVariable("COSMOS_CONTAINER_TRADING_PARTNERS")
            ?? "TradingPartners";

        _collection = database.GetCollection<TradingPartner>(collectionName);
        _logger = logger;
    }

    public async Task<TradingPartner?> GetAsync(string tenantId, string tradingPartnerId, string environment)
    {
        RequireTenant(tenantId);
        // By the record's fields, not a recomputed id: records keep whichever id they
        // were saved with (the old ambiguous composite or TradingPartnerIds.For).
        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<TradingPartner>.Filter.Eq(x => x.TradingPartnerId, tradingPartnerId),
            Builders<TradingPartner>.Filter.Eq(x => x.Environment, environment)
        );

        var partner = await _collection.Find(filter).FirstOrDefaultAsync();

        if (partner is null)
        {
            _logger.LogWarning(
                "Trading partner not found: {TenantId}/{TradingPartnerId}/{Environment}",
                SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment));
        }

        return partner;
    }

    public async Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId)
    {
        RequireTenant(tenantId);
        var filter = Builders<TradingPartner>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).ToListAsync();
    }

    /// <summary>
    /// Inserts the record. A record with the same (tenant, partner id,
    /// environment) is refused with <see cref="DuplicateTradingPartnerException"/>:
    /// by the unique index on those fields (records with any id) and by the
    /// deterministic id.
    /// </summary>
    public async Task<TradingPartner> CreateAsync(TradingPartner partner)
    {
        RequireTenant(partner.TenantId);
        await EnsureIndexesAsync();
        try
        {
            await _collection.InsertOneAsync(partner);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateTradingPartnerException(partner.TradingPartnerId, partner.Environment, ex);
        }

        _logger.LogInformation(
            "Created trading partner: {Id} in partition {TenantId}",
            SanitizeForLog(partner.Id), SanitizeForLog(partner.TenantId));

        return partner;
    }

    public async Task<TradingPartner> UpdateAsync(TradingPartner partner)
    {
        RequireTenant(partner.TenantId);
        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.Id, partner.Id),
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, partner.TenantId)
        );

        var result = await _collection.ReplaceOneAsync(filter, partner);

        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException(
                $"Trading partner '{partner.Id}' was not found for tenant '{partner.TenantId}'.");
        }

        _logger.LogInformation(
            "Updated trading partner: {Id}",
            SanitizeForLog(partner.Id));

        return partner;
    }

    public async Task DeleteAsync(string id, string partitionKey)
    {
        RequireTenant(partitionKey);
        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.Id, id),
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, partitionKey)
        );

        await _collection.DeleteOneAsync(filter);

        _logger.LogWarning(
            "Deleted trading partner: {Id} from partition {PartitionKey}",
            SanitizeForLog(id), SanitizeForLog(partitionKey));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> IndexesEnsured = new();

    /// <summary>
    /// Unique (tenantId, tradingPartnerId, environment), created once per process
    /// and collection before the first insert. If existing data already violates
    /// it, the insert path still refuses duplicates by id and the failure is logged.
    /// </summary>
    private async Task EnsureIndexesAsync()
    {
        var key = _collection.CollectionNamespace.FullName;
        if (IndexesEnsured.ContainsKey(key))
            return;
        try
        {
            await _collection.Indexes.CreateOneAsync(new CreateIndexModel<TradingPartner>(
                Builders<TradingPartner>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.TradingPartnerId)
                    .Ascending(x => x.Environment),
                new CreateIndexOptions { Unique = true, Name = "tenant_partner_environment_unique" }));
            IndexesEnsured[key] = true;
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex,
                "Could not create the unique (tenantId, tradingPartnerId, environment) index on trading partners; duplicates are refused by id only");
        }
    }

    private static string RequireTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId)
            ? throw new InvalidOperationException("A tenant is required; trading partners are never read or written without one.")
            : tenantId;

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove newline characters to prevent log forging via line injection.
        return value
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }
}
