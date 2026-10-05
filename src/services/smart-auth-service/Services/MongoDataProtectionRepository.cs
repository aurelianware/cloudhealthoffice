using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SmartAuthService.Services;

/// <summary>
/// The ASP.NET Core Data Protection key ring, shared by every pod through
/// MongoDB. The sign-in session cookie and the external login's state,
/// correlation and nonce values are protected with it, so a sign-in that
/// starts on one pod and returns from the IdP to another still completes.
/// Outside Development the keys are encrypted with the active
/// <c>SmartAuth:EncryptionCertificates</c> certificate (see Program.cs).
/// </summary>
public sealed class MongoDataProtectionRepository : IXmlRepository
{
    public const string CollectionName = "smart_dataprotection_keys";

    private readonly IMongoCollection<BsonDocument> _keys;

    public MongoDataProtectionRepository(IMongoDatabase database)
        => _keys = database.GetCollection<BsonDocument>(CollectionName);

    public IReadOnlyCollection<XElement> GetAllElements()
        => _keys.Find(FilterDefinition<BsonDocument>.Empty).ToList()
            .Select(d => XElement.Parse(d["xml"].AsString))
            .ToList();

    public void StoreElement(XElement element, string friendlyName)
    {
        var id = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        _keys.ReplaceOne(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            new BsonDocument
            {
                ["_id"] = id,
                ["xml"] = element.ToString(SaveOptions.DisableFormatting),
                ["storedAt"] = DateTime.UtcNow,
            },
            new ReplaceOptions { IsUpsert = true });
    }
}
