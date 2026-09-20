using AttachmentService.Models;
using MongoDB.Driver;

namespace AttachmentService.Services;

public class MongoTradingPartnerLookup : ITradingPartnerLookup
{
    private readonly IMongoCollection<TradingPartner> _collection;

    public MongoTradingPartnerLookup(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["MongoDb:TradingPartnersCollectionName"]
            ?? configuration["CosmosDb:TradingPartnersContainerName"]
            ?? "TradingPartners";
        _collection = database.GetCollection<TradingPartner>(collectionName);
    }

    public async Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId)
    {
        var filter = Builders<TradingPartner>.Filter.And(
            Builders<TradingPartner>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<TradingPartner>.Filter.Eq(x => x.PartnerId, payerId),
            Builders<TradingPartner>.Filter.Eq(x => x.IsActive, true)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }
}
