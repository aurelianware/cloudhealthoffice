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
    protected override async Task<(IPremiumInvoiceRepository Invoices, IRateTableRepository RateTables)> CreateStoresAsync(IHttpContextAccessor http)
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var database = await cosmos.CreateDatabaseAsync(client, "pb_rated_billing");
        await CosmosEmulatorFixture.CreateContainerAsync(database, "PremiumInvoices");
        await CosmosEmulatorFixture.CreateContainerAsync(database, RateTableRepositoryCosmos.ContainerName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CosmosDb:DatabaseName"] = database.Id })
            .Build();

        return (new PremiumInvoiceRepository(client, configuration, http, NullLogger<PremiumInvoiceRepository>.Instance),
            new RateTableRepositoryCosmos(client, configuration, http));
    }

    public override Task DisposeAsync() => Task.CompletedTask;
}
