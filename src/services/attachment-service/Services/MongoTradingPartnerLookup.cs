using AttachmentService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AttachmentService.Services;

/// <summary>
/// Reads trading partner configuration from the shared <c>TradingPartners</c> collection.
/// <para>
/// That collection is owned by trading-partner-service and seeded with the canonical document
/// shape — <c>tradingPartnerId</c>, <c>status</c>, <c>x12Config</c>. This service's
/// <see cref="TradingPartner"/> model is a different, attachment-era projection of the same
/// records, so the document is queried and mapped by its stored field names rather than by
/// deserialising straight onto the model, which would silently produce an empty partner.
/// </para>
/// </summary>
public class MongoTradingPartnerLookup : ITradingPartnerLookup
{
    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoTradingPartnerLookup(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["MongoDb:TradingPartnersCollectionName"]
            ?? configuration["CosmosDb:TradingPartnersContainerName"]
            ?? "TradingPartners";
        _collection = database.GetCollection<BsonDocument>(collectionName);
    }

    public async Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
            Builders<BsonDocument>.Filter.Eq("tradingPartnerId", payerId),
            Builders<BsonDocument>.Filter.Eq("status", "Active")
        );

        var document = await _collection.Find(filter).FirstOrDefaultAsync();

        return document is null ? null : Map(document);
    }

    private static TradingPartner Map(BsonDocument document)
    {
        var x12Config = document.TryGetValue("x12Config", out var raw) && raw is BsonDocument config
            ? config
            : null;

        return new TradingPartner
        {
            Id = Text(document, "id") ?? Text(document, "_id") ?? string.Empty,
            TenantId = Text(document, "tenantId") ?? string.Empty,
            PartnerId = Text(document, "tradingPartnerId") ?? string.Empty,
            PartnerName = Text(document, "partnerName") ?? string.Empty,
            IsActive = string.Equals(Text(document, "status"), "Active", StringComparison.OrdinalIgnoreCase),
            InterchangeSenderId = x12Config is null ? null : Text(x12Config, "senderId"),
            InterchangeReceiverId = x12Config is null ? null : Text(x12Config, "receiverId"),

            // The canonical document carries no acknowledgment preferences, so the model defaults
            // stand — 999, which is what GetAcknowledgmentType would fall back to anyway.
        };
    }

    private static string? Text(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && !value.IsBsonNull
            ? value.ToString()
            : null;
}
