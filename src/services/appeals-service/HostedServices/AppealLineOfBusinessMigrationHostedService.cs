using AppealsService.Models;
using AppealsService.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AppealsService.HostedServices;

/// <summary>
/// One-shot, idempotent rewrite of appeals stored before the line-of-business
/// consolidation (docs/architecture/line-of-business.md):
/// <list type="bullet">
///   <item><c>MedicarePartD</c> (or the old enum's number 5, which only ever meant
///   Part D in appeals) becomes <c>Medicare</c> with <c>BenefitType = PartDDrug</c>,
///   so the case keeps its Part D clocks.</item>
///   <item><c>Marketplace</c> becomes <c>Exchange</c>.</item>
/// </list>
/// Runs in <see cref="StartAsync"/>, before the web server starts taking requests, so no
/// request reads a Part D case as Part C. The shared <c>LineOfBusinessBsonSerializer</c>
/// reads the old names too, but it cannot restore the benefit type: this rewrite must run.
/// Both the class-map element name and its camelCase spelling are rewritten.
/// </summary>
public sealed class AppealLineOfBusinessMigrationHostedService(
    IMongoDatabase db,
    ILogger<AppealLineOfBusinessMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var raw = db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);
        var (partD, exchange) = await RunAsync(raw, cancellationToken);
        if (partD + exchange > 0)
            logger.LogInformation(
                "AppealLineOfBusinessMigration: {PartD} Part D appeals moved to Medicare + PartDDrug, {Exchange} Marketplace appeals to Exchange",
                partD, exchange);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Rewrites the legacy values; returns how many documents changed of each kind.</summary>
    internal static async Task<(long PartD, long Exchange)> RunAsync(IMongoCollection<BsonDocument> raw, CancellationToken ct)
    {
        long partD = 0, exchange = 0;
        foreach (var (lobField, benefitField) in FieldPairs())
        {
            var partDFilter = new BsonDocument(lobField, new BsonDocument("$in", new BsonArray { "MedicarePartD", "medicarePartD", 5 }));
            var partDUpdate = new BsonDocument("$set", new BsonDocument
            {
                { lobField, LineOfBusiness.Medicare.ToString() },
                { benefitField, AppealBenefitType.PartDDrug.ToString() },
            });
            partD += (await raw.UpdateManyAsync(partDFilter, partDUpdate, cancellationToken: ct)).ModifiedCount;

            var exchangeFilter = new BsonDocument(lobField, new BsonDocument("$in", new BsonArray { "Marketplace", "marketplace" }));
            var exchangeUpdate = new BsonDocument("$set", new BsonDocument(lobField, LineOfBusiness.Exchange.ToString()));
            exchange += (await raw.UpdateManyAsync(exchangeFilter, exchangeUpdate, cancellationToken: ct)).ModifiedCount;
        }
        return (partD, exchange);
    }

    /// <summary>(line of business, benefit type) element names: the class map's, then camelCase when different.</summary>
    private static IEnumerable<(string LineOfBusiness, string BenefitType)> FieldPairs()
    {
        var map = BsonClassMap.LookupClassMap(typeof(Appeal));
        var lob = map.GetMemberMap(nameof(Appeal.LineOfBusiness)).ElementName;
        var benefit = map.GetMemberMap(nameof(Appeal.BenefitType)).ElementName;
        yield return (lob, benefit);
        var camelLob = char.ToLowerInvariant(lob[0]) + lob[1..];
        if (camelLob != lob)
            yield return (camelLob, char.ToLowerInvariant(benefit[0]) + benefit[1..]);
    }
}
