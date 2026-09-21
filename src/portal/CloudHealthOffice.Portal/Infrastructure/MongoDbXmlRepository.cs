using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
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
    private readonly ILogger<MongoDbXmlRepository>? _logger;

    public MongoDbXmlRepository(
        IMongoClient mongoClient,
        IConfiguration configuration,
        ILogger<MongoDbXmlRepository>? logger = null)
    {
        // Takes the configured name rather than hard-coding one. MongoDB refuses to create a
        // case-variant of an existing database, so a lowercase name here would collide with the
        // CloudHealthOffice database the services use — whichever started first would win and the
        // other would fail outright.
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? configuration["MongoDb:DatabaseName"]
            ?? "CloudHealthOffice";

        var db = mongoClient.GetDatabase(databaseName);
        _collection = db.GetCollection<DataProtectionKeyDocument>("dataprotection_keys");
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var docs = _collection.Find(_ => true).ToList(cts.Token);
            return docs
                .Select(d =>
                {
                    try { return XElement.Parse(d.Xml); }
                    catch { return null; }
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DataProtection: failed to load keys from MongoDB. Keys will be regenerated — existing sessions may be invalidated.");
            return Array.Empty<XElement>();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _collection.InsertOne(new DataProtectionKeyDocument
            {
                FriendlyName = friendlyName,
                Xml = element.ToString(SaveOptions.DisableFormatting)
            }, cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "DataProtection: failed to persist key '{FriendlyName}' to MongoDB.", friendlyName);
            throw; // re-throw so DataProtection knows storage failed
        }
    }
}

public class DataProtectionKeyDocument
{
    public ObjectId Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string Xml { get; set; } = string.Empty;
}
