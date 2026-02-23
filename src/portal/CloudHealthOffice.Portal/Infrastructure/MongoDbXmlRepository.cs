using Microsoft.AspNetCore.DataProtection.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Xml.Linq;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Persists ASP.NET Core DataProtection keys to MongoDB so that all
/// portal replicas share the same key ring.  Without this, each pod
/// generates its own ephemeral keys and cannot decrypt cookies / auth
/// tokens issued by a different pod.
/// </summary>
public class MongoDbXmlRepository : IXmlRepository
{
    private readonly IMongoCollection<DataProtectionKeyDocument> _collection;

    public MongoDbXmlRepository(IMongoClient mongoClient)
    {
        var db = mongoClient.GetDatabase("cloudhealthoffice");
        _collection = db.GetCollection<DataProtectionKeyDocument>("dataprotection_keys");
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var docs = _collection.Find(_ => true).ToList();
        return docs
            .Select(d => XElement.Parse(d.Xml))
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        _collection.InsertOne(new DataProtectionKeyDocument
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting)
        });
    }
}

public class DataProtectionKeyDocument
{
    public ObjectId Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string Xml { get; set; } = string.Empty;
}
