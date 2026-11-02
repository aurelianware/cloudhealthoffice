using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Programs;

/// <summary>
/// BSON for <see cref="LineOfBusiness"/> in a service that used to store its own unnumbered
/// enum as a number (payment-service 0-3, eligibility-service 0-5). Writes the shared name.
/// Reads a name as <see cref="LineOfBusinessBsonSerializer"/> does, and reads a stored
/// number through <see cref="LegacyOrder"/>, because such a service never stored the
/// shared numbers: a document reads correctly before
/// <see cref="LegacyLineOfBusinessMigrationHostedService"/> has rewritten it.
/// Register it only in the service whose old numbers <see cref="LegacyOrder"/> describes.
/// </summary>
public sealed class LegacyLineOfBusinessBsonSerializer(IReadOnlyList<LineOfBusiness> legacyOrder)
    : SerializerBase<LineOfBusiness>
{
    private static readonly LineOfBusinessBsonSerializer Shared = new(BsonType.String);

    /// <summary>The old enum's members in their stored order: index n is what n meant.</summary>
    public IReadOnlyList<LineOfBusiness> LegacyOrder { get; } = legacyOrder;

    public override LineOfBusiness Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        return reader.GetCurrentBsonType() switch
        {
            BsonType.Int32 => FromLegacy(reader.ReadInt32()),
            BsonType.Int64 => FromLegacy(reader.ReadInt64()),
            _ => Shared.Deserialize(context, args),
        };
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, LineOfBusiness value)
        => Shared.Serialize(context, args, value);

    /// <summary>The line of business old number <paramref name="n"/> meant; Unknown when none.</summary>
    public LineOfBusiness FromLegacy(long n) => n >= 0 && n < LegacyOrder.Count ? LegacyOrder[(int)n] : LineOfBusiness.Unknown;
}

/// <summary>
/// One-shot, idempotent startup rewrite of a service's old stored line-of-business numbers
/// to the shared names (docs/architecture/line-of-business.md). Each number n becomes the
/// name of <c>LegacyOrder[n]</c>, and a stored <c>"Marketplace"</c> becomes <c>"Exchange"</c>.
/// Only numbers are rewritten and every rewrite writes a name, so a second run changes nothing.
/// </summary>
/// <remarks>
/// Reads do not depend on it (<see cref="LegacyLineOfBusinessBsonSerializer"/> reads an old
/// number correctly), so a failure, such as the database not being reachable at startup,
/// is logged and the rewrite is tried again on the next start. With
/// <c>MongoDb:UseTenantScoping</c> every <c>{DatabaseName}_{tenant}</c> database is rewritten
/// too. A host without a Mongo client (API test hosts) skips it.
/// </remarks>
public abstract class LegacyLineOfBusinessMigrationHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger logger) : IHostedService
{
    /// <summary>The collection holding the documents.</summary>
    protected abstract string CollectionName { get; }

    /// <summary>The old enum's members in their stored order.</summary>
    protected abstract IReadOnlyList<LineOfBusiness> LegacyOrder { get; }

    /// <summary>Dotted element paths holding the line of business (see <see cref="WithCamelCase"/>).</summary>
    protected abstract IEnumerable<string> FieldPaths();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IMongoClient>() is not { } client) return;
        try
        {
            foreach (var name in await DatabaseNamesAsync(client, configuration, cancellationToken))
            {
                var raw = client.GetDatabase(name).GetCollection<BsonDocument>(CollectionName);
                var changed = await RewriteAsync(raw, FieldPaths(), LegacyOrder, cancellationToken);
                if (changed > 0)
                    logger.LogInformation(
                        "LineOfBusinessMigration: {Count} documents in {Database}.{Collection} moved to the shared line-of-business names",
                        changed, name, CollectionName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "LineOfBusinessMigration of {Collection} did not complete; documents still read correctly and it is retried on the next start",
                CollectionName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Rewrites the old numbers (and "Marketplace") in one collection; returns how many documents changed.</summary>
    public static async Task<long> RewriteAsync(
        IMongoCollection<BsonDocument> raw, IEnumerable<string> fieldPaths, IReadOnlyList<LineOfBusiness> legacyOrder, CancellationToken ct)
    {
        long changed = 0;
        foreach (var field in fieldPaths)
        {
            for (var n = 0; n < legacyOrder.Count; n++)
            {
                // A numeric equality matches Int32, Int64 and Double alike.
                var filter = new BsonDocument(field, n);
                var update = new BsonDocument("$set", new BsonDocument(field, legacyOrder[n].ToString()));
                changed += (await raw.UpdateManyAsync(filter, update, cancellationToken: ct)).ModifiedCount;
            }

            var marketplace = new BsonDocument(field, new BsonDocument("$in", new BsonArray { "Marketplace", "marketplace" }));
            var exchange = new BsonDocument("$set", new BsonDocument(field, LineOfBusiness.Exchange.ToString()));
            changed += (await raw.UpdateManyAsync(marketplace, exchange, cancellationToken: ct)).ModifiedCount;
        }
        return changed;
    }

    /// <summary>The base database, then each tenant database when tenant scoping is on.</summary>
    public static async Task<IReadOnlyList<string>> DatabaseNamesAsync(IMongoClient client, IConfiguration configuration, CancellationToken ct)
    {
        var baseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        var databases = new List<string> { baseName };
        if (configuration.GetValue<bool>("MongoDb:UseTenantScoping", false))
        {
            var names = await (await client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
            databases.AddRange(names.Where(n => n.StartsWith(baseName + "_", StringComparison.Ordinal)));
        }
        return databases;
    }

    /// <summary><paramref name="path"/>, then its camelCase spelling (each segment) when different.</summary>
    public static IEnumerable<string> WithCamelCase(string path)
    {
        yield return path;
        var camel = string.Join('.', path.Split('.').Select(s => char.ToLowerInvariant(s[0]) + s[1..]));
        if (camel != path) yield return camel;
    }

    /// <summary>The class-map element name of <paramref name="member"/> on <typeparamref name="T"/>.</summary>
    public static string ElementName<T>(string member) => BsonClassMap.LookupClassMap(typeof(T)).GetMemberMap(member).ElementName;
}
