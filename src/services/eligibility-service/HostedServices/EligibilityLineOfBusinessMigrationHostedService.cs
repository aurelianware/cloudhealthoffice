using EligibilityService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EligibilityService.HostedServices;

/// <summary>
/// Rewrites eligibility inquiries stored before the line-of-business consolidation
/// (docs/architecture/line-of-business.md). eligibility-service had its own unnumbered
/// enum, so the driver stored <c>LineOfBusiness</c> as 0-5 (Commercial … VA); the shared
/// enum numbers them 1-6. See <see cref="LegacyLineOfBusinessMigrationHostedService"/> for
/// how the rewrite runs. Registered ahead of the batch worker.
/// </summary>
public sealed class EligibilityLineOfBusinessMigrationHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<EligibilityLineOfBusinessMigrationHostedService> logger)
    : LegacyLineOfBusinessMigrationHostedService(services, configuration, logger)
{
    /// <summary>The collection <c>EligibilityRepositoryMongo</c> stores inquiries in.</summary>
    public const string Collection = "EligibilityInquiries";

    /// <summary>eligibility-service's old enum, in its stored order (the shared order, from 0).</summary>
    public static IReadOnlyList<LineOfBusiness> EligibilityLegacyOrder { get; } = LineOfBusinessNames.All;

    /// <summary>How eligibility-service stores an inquiry's line of business (registered in Program.cs).</summary>
    public static LegacyLineOfBusinessBsonSerializer Serializer { get; } = new(EligibilityLegacyOrder);

    protected override string CollectionName => Collection;
    protected override IReadOnlyList<LineOfBusiness> LegacyOrder => EligibilityLegacyOrder;
    protected override IEnumerable<string> FieldPaths() => Paths();

    /// <summary><c>LineOfBusiness</c> by the class map, and its camelCase spelling.</summary>
    public static IEnumerable<string> Paths() =>
        WithCamelCase(ElementName<EligibilityInquiry>(nameof(EligibilityInquiry.LineOfBusiness)));

    /// <summary>Rewrites one collection; returns how many documents changed.</summary>
    public static Task<long> RunAsync(IMongoCollection<BsonDocument> raw, CancellationToken ct)
        => RewriteAsync(raw, Paths(), EligibilityLegacyOrder, ct);
}
