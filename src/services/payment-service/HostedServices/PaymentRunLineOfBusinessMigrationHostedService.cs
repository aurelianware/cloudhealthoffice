using PaymentService.Models;

namespace PaymentService.HostedServices;

/// <summary>
/// Rewrites payment runs stored before the line-of-business consolidation
/// (docs/architecture/line-of-business.md). payment-service had its own unnumbered enum,
/// so the driver stored <c>Criteria.LineOfBusiness</c> as 0-3 (Commercial, Medicare,
/// Medicaid, Marketplace); the shared enum numbers them 1-4. See
/// <see cref="LegacyLineOfBusinessMigrationHostedService"/> for how the rewrite runs.
/// Registered ahead of the other hosted services (the GL dispatcher, the reservation job).
/// </summary>
public sealed class PaymentRunLineOfBusinessMigrationHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PaymentRunLineOfBusinessMigrationHostedService> logger)
    : LegacyLineOfBusinessMigrationHostedService(services, configuration, logger)
{
    /// <summary>The collection <c>PaymentRunRepositoryMongo</c> and the GL dispatcher use.</summary>
    public const string Collection = "PaymentRuns";

    /// <summary>payment-service's old enum, in its stored order.</summary>
    public static IReadOnlyList<LineOfBusiness> PaymentLegacyOrder { get; } =
        [LineOfBusiness.Commercial, LineOfBusiness.Medicare, LineOfBusiness.Medicaid, LineOfBusiness.Exchange];

    /// <summary>How payment-service stores a run's line of business (registered in Program.cs).</summary>
    public static LegacyLineOfBusinessBsonSerializer Serializer { get; } = new(PaymentLegacyOrder);

    protected override string CollectionName => Collection;
    protected override IReadOnlyList<LineOfBusiness> LegacyOrder => PaymentLegacyOrder;

    protected override IEnumerable<string> FieldPaths() => Paths();

    /// <summary><c>Criteria.LineOfBusiness</c> by the class maps, and its camelCase spelling.</summary>
    public static IEnumerable<string> Paths() => WithCamelCase(
        $"{ElementName<PaymentRun>(nameof(PaymentRun.Criteria))}.{ElementName<PaymentRunCriteria>(nameof(PaymentRunCriteria.LineOfBusiness))}");

    /// <summary>Rewrites one collection; returns how many documents changed.</summary>
    public static Task<long> RunAsync(MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> raw, CancellationToken ct)
        => RewriteAsync(raw, Paths(), PaymentLegacyOrder, ct);
}
