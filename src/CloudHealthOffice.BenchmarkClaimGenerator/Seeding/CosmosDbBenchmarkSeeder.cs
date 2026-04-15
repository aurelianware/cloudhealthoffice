using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Seeding;

/// <summary>
/// Cosmos DB adapter for the benchmark data seeder interface.
/// Wraps <see cref="CosmosDbSeeder"/>, whose <c>WriteDocumentsAsync</c> is a no-op stub by default.
/// To actually persist to Cosmos DB, subclass <see cref="CosmosDbSeeder"/> and override
/// <c>WriteDocumentsAsync</c> with an Azure.Cosmos SDK bulk-write implementation,
/// then pass that subclass instance here.
/// </summary>
public class CosmosDbBenchmarkSeeder : IBenchmarkDataSeeder
{
    private readonly CosmosDbSeeder _seeder;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDbBenchmarkSeeder"/> class.
    /// </summary>
    /// <param name="connectionString">Cosmos DB connection string.</param>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="databaseName">Database name. Default: "cloudhealthoffice".</param>
    /// <param name="logger">Optional logger.</param>
    public CosmosDbBenchmarkSeeder(
        string connectionString,
        string tenantId = "mcc-benchmark",
        string databaseName = "cloudhealthoffice",
        ILogger? logger = null)
    {
        _seeder = new CosmosDbSeeder(connectionString, tenantId, databaseName, logger);
    }

    /// <inheritdoc />
    public Task<int> SeedBenefitPlansAsync(
        List<SyntheticBenefitPlan> plans,
        CancellationToken cancellationToken = default)
        => _seeder.SeedBenefitPlansAsync(plans, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedFeeSchedulesAsync(
        List<SyntheticFeeSchedule> feeSchedules,
        CancellationToken cancellationToken = default)
        => _seeder.SeedFeeSchedulesAsync(feeSchedules, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedProvidersAsync(
        List<SyntheticProvider> providers,
        CancellationToken cancellationToken = default)
        => _seeder.SeedProvidersAsync(providers, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedProviderContractsAsync(
        List<SyntheticProviderContract> contracts,
        CancellationToken cancellationToken = default)
        => _seeder.SeedProviderContractsAsync(contracts, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedMembersAsync(
        List<SyntheticMember> members,
        CancellationToken cancellationToken = default)
        => _seeder.SeedMembersAsync(members, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedCoveragesAsync(
        List<SyntheticMember> members,
        CancellationToken cancellationToken = default)
        => _seeder.SeedCoveragesAsync(members, cancellationToken);

    /// <inheritdoc />
    public Task<int> SeedAccumulatorsAsync(
        List<SyntheticAccumulator> accumulators,
        CancellationToken cancellationToken = default)
        => _seeder.SeedAccumulatorsAsync(accumulators, cancellationToken);
}
