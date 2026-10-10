using CloudHealthOffice.Testing.Cosmos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Middleware;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Remittance;

/// <summary>
/// <see cref="CashApplicationScenarios"/> on the Cosmos DB emulator with the
/// Cosmos repositories (<see cref="PremiumInvoiceRepository"/>,
/// <see cref="RemittanceBatchRepositoryCosmos"/>,
/// <see cref="RemittanceExceptionRepositoryCosmos"/>,
/// <see cref="SponsorAccountRepositoryCosmos"/>): the remittance containers'
/// create-conflict de-duplication, the ETag-pinned invoice / sponsor-account
/// writes and the exception queue's conditional transition, evaluated by a
/// real server. Containers are partitioned on /tenantId as Program.cs
/// documents; documents go through the service's own serializer.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class CashApplicationCosmosEmulatorTests(CosmosEmulatorFixture cosmos) : CashApplicationScenarios
{
    protected override async Task<Stores> CreateStoresAsync(IHttpContextAccessor http)
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var database = await cosmos.CreateDatabaseAsync(client, "pb_cash_application");
        foreach (var name in new[] { "PremiumInvoices", "RemittanceBatches", "RemittanceExceptions", "SponsorAccounts" })
            await CosmosEmulatorFixture.CreateContainerAsync(database, name);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CosmosDb:DatabaseName"] = database.Id })
            .Build();

        return new Stores(
            new PremiumInvoiceRepository(client, configuration, http, NullLogger<PremiumInvoiceRepository>.Instance),
            new RemittanceBatchRepositoryCosmos(client, configuration, http),
            new RemittanceExceptionRepositoryCosmos(client, configuration, http),
            new SponsorAccountRepositoryCosmos(client, configuration, http));
    }

    public override Task DisposeAsync() => Task.CompletedTask;
}
