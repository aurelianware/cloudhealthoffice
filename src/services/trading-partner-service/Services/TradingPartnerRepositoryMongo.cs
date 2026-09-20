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
        // Document ids keep the composite shape used by the Cosmos repository so the two
        // implementations address the same records.
        var id = $"{tradingPartnerId}-{tenantId}-{environment}";

        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.Id, id),
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, tenantId)
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
        var filter = Builders<TradingPartner>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<TradingPartner> CreateAsync(TradingPartner partner)
    {
        await _collection.InsertOneAsync(partner);

        _logger.LogInformation(
            "Created trading partner: {Id} in partition {TenantId}",
            SanitizeForLog(partner.Id), SanitizeForLog(partner.TenantId));

        return partner;
    }

    public async Task<TradingPartner> UpdateAsync(TradingPartner partner)
    {
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
        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.Id, id),
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, partitionKey)
        );

        await _collection.DeleteOneAsync(filter);

        _logger.LogWarning(
            "Deleted trading partner: {Id} from partition {PartitionKey}",
            SanitizeForLog(id), SanitizeForLog(partitionKey));
    }

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
