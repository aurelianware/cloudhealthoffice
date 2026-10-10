using CloudHealthOffice.Testing.Cosmos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Middleware;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.RatedBilling;

/// <summary>
/// <see cref="RatedBillingScenarios"/> on the Cosmos DB emulator with the Cosmos
/// repositories (<see cref="PremiumInvoiceRepository"/>, <see cref="RateTableRepositoryCosmos"/>):
/// the deterministic invoice id's create conflict (409), ETag-pinned draft
/// regeneration, string-stored statuses in the overdue/status queries, and
/// rate table versions, evaluated by a real server. Containers are partitioned
/// on /tenantId as Program.cs documents; documents go through the service's own serializer.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class RatedBillingCosmosEmulatorTests(CosmosEmulatorFixture cosmos) : RatedBillingScenarios
{
    protected override async Task<Stores> CreateStoresAsync(IHttpContextAccessor http)
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var database = await cosmos.CreateDatabaseAsync(client, "pb_rated_billing");
        await CosmosEmulatorFixture.CreateContainerAsync(database, "PremiumInvoices");
        await CosmosEmulatorFixture.CreateContainerAsync(database, RateTableRepositoryCosmos.ContainerName);
        await CosmosEmulatorFixture.CreateContainerAsync(database, BillingLeaseRepositoryCosmos.ContainerName);
        await CosmosEmulatorFixture.CreateContainerAsync(database, "SponsorAccounts");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CosmosDb:DatabaseName"] = database.Id })
            .Build();

        return new Stores(
            new PremiumInvoiceRepository(client, configuration, http, NullLogger<PremiumInvoiceRepository>.Instance),
            new RateTableRepositoryCosmos(client, configuration, http),
            new BillingLeaseRepositoryCosmos(client, configuration, http),
            new SponsorAccountRepositoryCosmos(client, configuration, http));
    }

    public override Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task TeardownDatabaseDelete_IsBestEffort_AndNeverThrows()
    {
        // CI saw the vnext emulator answer a database delete with 500 (PostgreSQL deadlock E40P01),
        // which failed every test of the collection at cleanup. Cleanup now retries and then gives up quietly.
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient();
        await CosmosEmulatorFixture.DeleteDatabaseBestEffortAsync(client, $"never_created_{Guid.NewGuid():N}");

        var broken = new Microsoft.Azure.Cosmos.CosmosClient(CosmosEmulator.Endpoint, CosmosEmulator.Key);
        broken.Dispose();
        var delete = () => CosmosEmulatorFixture.DeleteDatabaseBestEffortAsync(broken, "anything", TimeSpan.FromMilliseconds(1));
        await delete.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task StoredStatusIsAString_SoAnEnumNumberNeverMatchesIt()
    {
        // Why the repository compares with StoredStatus: the service's serializer writes "sent", "voided"…
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var database = await cosmos.CreateDatabaseAsync(client, "pb_status_strings");
        var container = await CosmosEmulatorFixture.CreateContainerAsync(database, "PremiumInvoices");
        await container.CreateItemAsync(new PremiumBillingService.Models.PremiumInvoice
        {
            Id = "x1", TenantId = "t", InvoiceNumber = "INV-X", GroupNumber = "G", Status = PremiumBillingService.Models.InvoiceStatus.Sent
        }, new Microsoft.Azure.Cosmos.PartitionKey("t"));

        async Task<int> Count(object status)
        {
            var iterator = container.GetItemQueryIterator<PremiumBillingService.Models.PremiumInvoice>(
                new Microsoft.Azure.Cosmos.QueryDefinition("SELECT * FROM c WHERE c.tenantId = 't' AND c.status = @s").WithParameter("@s", status));
            var n = 0;
            while (iterator.HasMoreResults) n += (await iterator.ReadNextAsync()).Count;
            return n;
        }

        (await Count((int)PremiumBillingService.Models.InvoiceStatus.Sent)).Should().Be(0);
        (await Count(PremiumInvoiceRepository.StoredStatus(PremiumBillingService.Models.InvoiceStatus.Sent))).Should().Be(1);
    }
}
