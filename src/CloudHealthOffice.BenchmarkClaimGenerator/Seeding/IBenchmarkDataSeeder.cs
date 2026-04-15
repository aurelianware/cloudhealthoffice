using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Seeding;

/// <summary>
/// Interface for seeding benchmark data into a backend data store.
/// Implementations include Cosmos DB and in-memory for testing.
/// </summary>
public interface IBenchmarkDataSeeder
{
    /// <summary>Seed benefit plan configurations.</summary>
    Task<int> SeedBenefitPlansAsync(List<SyntheticBenefitPlan> plans, CancellationToken cancellationToken = default);

    /// <summary>Seed fee schedule definitions.</summary>
    Task<int> SeedFeeSchedulesAsync(List<SyntheticFeeSchedule> feeSchedules, CancellationToken cancellationToken = default);

    /// <summary>Seed provider records.</summary>
    Task<int> SeedProvidersAsync(List<SyntheticProvider> providers, CancellationToken cancellationToken = default);

    /// <summary>Seed provider contract records.</summary>
    Task<int> SeedProviderContractsAsync(List<SyntheticProviderContract> contracts, CancellationToken cancellationToken = default);

    /// <summary>Seed member records (subscribers + dependents).</summary>
    Task<int> SeedMembersAsync(List<SyntheticMember> members, CancellationToken cancellationToken = default);

    /// <summary>Seed coverage records for all members.</summary>
    Task<int> SeedCoveragesAsync(List<SyntheticMember> members, CancellationToken cancellationToken = default);

    /// <summary>Seed accumulator balance records.</summary>
    Task<int> SeedAccumulatorsAsync(List<SyntheticAccumulator> accumulators, CancellationToken cancellationToken = default);
}
