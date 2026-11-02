using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.HostedServices;

/// <summary>
/// One-shot, idempotent rewrite of payment runs stored before the line-of-business
/// consolidation (docs/architecture/line-of-business.md). payment-service had its own
/// unnumbered enum, so the driver stored <c>Criteria.LineOfBusiness</c> as 0-3
/// (Commercial, Medicare, Medicaid, Marketplace), while the shared enum numbers them 1-4.
/// Each stored number becomes the shared name (0 → Commercial … 3 → Exchange); a stored
/// <c>"Marketplace"</c> becomes <c>"Exchange"</c>.
/// </summary>
/// <remarks>
/// Only numbers are rewritten and every rewrite writes a name, so a second run changes
/// nothing. Reads do not depend on it: <see cref="PaymentRunLineOfBusinessBsonSerializer"/>
/// reads a stored number as the old enum. So a failure (the database not reachable at
/// startup) is logged and the rewrite is tried again on the next start. With
/// <c>MongoDb:UseTenantScoping</c> every <c>{DatabaseName}_{tenant}</c> database is
/// rewritten too. A host without a Mongo client (the API test hosts) skips it.
/// </remarks>
public sealed class PaymentRunLineOfBusinessMigrationHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PaymentRunLineOfBusinessMigrationHostedService> logger) : IHostedService
{
    /// <summary>The collection <c>PaymentRunRepositoryMongo</c> and the GL dispatcher use.</summary>
    public const string CollectionName = "PaymentRuns";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IMongoClient>() is not { } client) return;
        try
        {
            await MigrateAsync(client, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "PaymentRunLineOfBusinessMigration did not complete; runs still read correctly and it is retried on the next start");
        }
    }

    private async Task MigrateAsync(IMongoClient client, CancellationToken cancellationToken)
    {

        var baseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        var databases = new List<string> { baseName };
        if (configuration.GetValue<bool>("MongoDb:UseTenantScoping", false))
        {
            var names = await (await client.ListDatabaseNamesAsync(cancellationToken)).ToListAsync(cancellationToken);
            databases.AddRange(names.Where(n => n.StartsWith(baseName + "_", StringComparison.Ordinal)));
        }

        foreach (var name in databases)
        {
            var raw = client.GetDatabase(name).GetCollection<BsonDocument>(CollectionName);
            var changed = await RunAsync(raw, cancellationToken);
            if (changed > 0)
                logger.LogInformation(
                    "PaymentRunLineOfBusinessMigration: {Count} payment runs in {Database} moved to the shared line-of-business names",
                    changed, name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Rewrites the legacy values in one collection; returns how many documents changed.</summary>
    public static async Task<long> RunAsync(IMongoCollection<BsonDocument> raw, CancellationToken ct)
    {
        long changed = 0;
        foreach (var field in FieldNames())
        {
            var legacy = PaymentRunLineOfBusinessBsonSerializer.LegacyOrder;
            for (var n = 0; n < legacy.Count; n++)
            {
                // A numeric equality matches Int32, Int64 and Double alike.
                var filter = new BsonDocument(field, n);
                var update = new BsonDocument("$set", new BsonDocument(field, legacy[n].ToString()));
                changed += (await raw.UpdateManyAsync(filter, update, cancellationToken: ct)).ModifiedCount;
            }

            var marketplace = new BsonDocument(field, new BsonDocument("$in", new BsonArray { "Marketplace", "marketplace" }));
            var exchange = new BsonDocument("$set", new BsonDocument(field, LineOfBusiness.Exchange.ToString()));
            changed += (await raw.UpdateManyAsync(marketplace, exchange, cancellationToken: ct)).ModifiedCount;
        }
        return changed;
    }

    /// <summary>The dotted element name from the class maps, then its camelCase spelling when different.</summary>
    private static IEnumerable<string> FieldNames()
    {
        var criteria = BsonClassMap.LookupClassMap(typeof(PaymentRun)).GetMemberMap(nameof(PaymentRun.Criteria)).ElementName;
        var lob = BsonClassMap.LookupClassMap(typeof(PaymentRunCriteria)).GetMemberMap(nameof(PaymentRunCriteria.LineOfBusiness)).ElementName;
        var name = $"{criteria}.{lob}";
        yield return name;
        var camel = $"{Camel(criteria)}.{Camel(lob)}";
        if (camel != name) yield return camel;
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
