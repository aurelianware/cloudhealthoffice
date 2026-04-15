using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class CosmosDbSeederTests
{
    [Fact]
    public void ContainerNames_MatchProductionServiceConfiguration()
    {
        Assert.Equal("Members", CosmosDbSeeder.ContainerNames.Members);
        Assert.Equal("Coverage", CosmosDbSeeder.ContainerNames.Coverages);
        Assert.Equal("Providers", CosmosDbSeeder.ContainerNames.Providers);
        Assert.Equal("ProviderContracts", CosmosDbSeeder.ContainerNames.ProviderContracts);
        Assert.Equal("BenefitPlans", CosmosDbSeeder.ContainerNames.BenefitPlans);
        Assert.Equal("FeeSchedules", CosmosDbSeeder.ContainerNames.FeeSchedules);
        Assert.Equal("Accumulators", CosmosDbSeeder.ContainerNames.Accumulators);
    }

    [Fact]
    public void BatchSize_Is100()
    {
        Assert.Equal(100, CosmosDbSeeder.BatchSize);
    }

    [Fact]
    public async Task SeedBenefitPlansAsync_CompletesWithoutError()
    {
        var seeder = new CosmosDbSeeder("unused-connection-string");
        var plans = SyntheticBenefitPlanGenerator.Generate(42);

        // Base implementation is a no-op (no actual Cosmos connection)
        var count = await seeder.SeedBenefitPlansAsync(plans);
        Assert.Equal(plans.Count, count);
    }

    [Fact]
    public async Task SeedFeeSchedulesAsync_CompletesWithoutError()
    {
        var seeder = new CosmosDbSeeder("unused-connection-string");
        var feeSchedules = SyntheticFeeScheduleGenerator.Generate(42);

        var count = await seeder.SeedFeeSchedulesAsync(feeSchedules);
        Assert.Equal(feeSchedules.Count, count);
    }

    [Fact]
    public async Task SeedProvidersAsync_CompletesWithoutError()
    {
        var seeder = new CosmosDbSeeder("unused-connection-string");
        var providerProfile = new ProviderPoolProfile
        {
            IndividualProviderCount = 10,
            OrganizationalProviderCount = 2,
            Seed = 42,
            FacilityDistribution = new FacilityDistribution
            {
                Hospitals = 1, Clinics = 1,
                SkilledNursingFacilities = 0, BehavioralHealth = 0,
            },
        };
        var providerGen = new SyntheticProviderGenerator();
        var providers = providerGen.Generate(providerProfile);

        var count = await seeder.SeedProvidersAsync(providers);
        Assert.Equal(providers.Count, count);
    }

    [Fact]
    public async Task SeedMembersAsync_CompletesWithoutError()
    {
        var seeder = new CosmosDbSeeder("unused-connection-string");
        var memberProfile = new MemberPoolProfile
        {
            SubscriberCount = 10,
            Seed = 42,
            TenantId = "test",
        };
        var plans = SyntheticBenefitPlanGenerator.Generate(42);
        var memberGen = new SyntheticMemberGenerator();
        var members = memberGen.Generate(memberProfile, plans);

        var count = await seeder.SeedMembersAsync(members);
        Assert.True(count >= 10); // At least 10 subscribers (plus dependents)
    }

    [Fact]
    public async Task SeedAccumulatorsAsync_CompletesWithoutError()
    {
        var seeder = new CosmosDbSeeder("unused-connection-string");
        var memberProfile = new MemberPoolProfile
        {
            SubscriberCount = 10,
            Seed = 42,
        };
        var plans = SyntheticBenefitPlanGenerator.Generate(42);
        var memberGen = new SyntheticMemberGenerator();
        var members = memberGen.Generate(memberProfile, plans);
        var accGen = new SyntheticAccumulatorGenerator();
        var accumulators = accGen.Generate(members, plans, 42);

        var count = await seeder.SeedAccumulatorsAsync(accumulators);
        Assert.True(count >= 0);
    }
}
