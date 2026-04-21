using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class BatchEligibilityStorageModeTests
{
    [Fact]
    public void InMemory_ExplicitlySelected_RegistersInMemoryStoreAndQueue()
    {
        var services = new ServiceCollection();
        services.AddBatchEligibilityStorage(Config("InMemory"), DevEnvironment());
        var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryBatchJobStore>(provider.GetRequiredService<IBatchJobStore>());
        Assert.IsType<InMemoryBatchQueue>(provider.GetRequiredService<IBatchQueue>());
        Assert.IsType<ChannelBatchQueueProcessor>(provider.GetRequiredService<IBatchQueueProcessor>());
    }

    [Fact]
    public void Persistent_WithFullConfig_RegistersCosmosAndMessageBusQueue()
    {
        var services = new ServiceCollection();
        services.AddBatchEligibilityStorage(Config("Persistent", full: true), ProdEnvironment());
        // Do NOT BuildServiceProvider: the Cosmos client would attempt real
        // network lookups. Inspect the registrations instead.

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBatchJobStore) && d.ImplementationFactory != null);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBatchQueue) && d.ImplementationFactory != null);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBatchQueueProcessor) && d.ImplementationFactory != null);
    }

    [Fact]
    public void Auto_InDevWithoutConfig_FallsBackToInMemory()
    {
        var services = new ServiceCollection();
        services.AddBatchEligibilityStorage(Config("Auto"), DevEnvironment());
        var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryBatchJobStore>(provider.GetRequiredService<IBatchJobStore>());
    }

    [Fact]
    public void Auto_InProdWithoutConfig_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddBatchEligibilityStorage(Config("Auto"), ProdEnvironment()));
    }

    [Fact]
    public void Persistent_MissingCosmosConnectionString_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddBatchEligibilityStorage(
                Config("Persistent", full: false, withBlob: true),
                ProdEnvironment()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IConfiguration Config(
        string mode, bool full = false,
        bool withCosmos = false, bool withBlob = false)
    {
        var dict = new Dictionary<string, string?>
        {
            ["BatchEligibility:StorageMode"] = mode
        };
        if (full || withCosmos)
            dict["BatchEligibility:CosmosDb:ConnectionString"] =
                "AccountEndpoint=https://fake.documents.azure.com:443/;AccountKey=ZmFrZQ==;";
        if (full || withBlob)
            dict["BatchEligibility:BlobStorage:ConnectionString"] =
                "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=ZmFrZQ==;" +
                "EndpointSuffix=core.windows.net";

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment DevEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }

    private static IHostEnvironment ProdEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        return env;
    }
}
