using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Remittance;

/// <summary>
/// The real pipeline (auth, controllers, cash application) with the invoice,
/// remittance and sponsor-account repositories on a real mongod: an 820
/// uploaded over HTTP changes the saved balances.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RemittanceEndpointTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string User = "finance-user-3";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private Factory _factory = null!;

    public RemittanceEndpointTests(MongoRunnerFixture mongo) => _mongo = mongo;

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly IMongoDatabase _database;

        public Factory(IMongoDatabase database) => _database = database;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPremiumInvoiceRepository>();
                services.RemoveAll<IRemittanceBatchRepository>();
                services.RemoveAll<IRemittanceExceptionRepository>();
                services.RemoveAll<ISponsorAccountRepository>();
                services.AddScoped<IPremiumInvoiceRepository>(sp => new PremiumInvoiceRepositoryMongo(
                    _database, sp.GetRequiredService<IHttpContextAccessor>(), NullLogger<PremiumInvoiceRepositoryMongo>.Instance));
                services.AddScoped<IRemittanceBatchRepository>(sp => new RemittanceBatchRepositoryMongo(_database, sp.GetRequiredService<IHttpContextAccessor>()));
                services.AddScoped<IRemittanceExceptionRepository>(sp => new RemittanceExceptionRepositoryMongo(_database, sp.GetRequiredService<IHttpContextAccessor>()));
                services.AddScoped<ISponsorAccountRepository>(sp => new SponsorAccountRepositoryMongo(_database, sp.GetRequiredService<IHttpContextAccessor>()));
            });
        }
    }

    private sealed class FixedTenant : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_remit_http");
        _factory = new Factory(_database);

        var http = new FixedTenant { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items["TenantId"] = Tenant;
        var invoices = new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance);
        foreach (var (number, amount) in new[] { ("INV-GRP001-2026-03", 1200m), ("INV-GRP001-2026-02", 500m) })
        {
            var invoice = new PremiumInvoice
            {
                InvoiceNumber = number,
                GroupNumber = "GRP001",
                BillingPeriodStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = InvoiceStatus.Sent,
                LineItems = { new InvoiceLineItem { MemberId = "MBR-1", TotalPremium = amount } }
            };
            invoice.RecalculateTotals();
            await invoices.CreateAsync(invoice);
        }
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _mongo.DropDatabaseAsync(_database);
    }

    private HttpClient Finance()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient WithPermissions(params string[] permissions)
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(User, Tenant, new[] { "BillingClerk" }, permissions);
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Edi(string body) => new(body, Encoding.UTF8, "application/edi-x12");

    private static string Sample820() => Synthetic820.Single(new Synthetic820.Transaction { Amount = 2000.00m, Trace = "HTTP-1" }
        .Organization()
        .Rmr("INV-GRP001-2026-03", 1200.00m) // exact
        .Rmr("INV-GRP001-2026-02", 700.00m)  // over by 200
        .Rmr("INV-UNKNOWN", 100.00m));       // unmatched

    [Fact]
    public async Task Finance_Uploads820_AndBalancesAreSaved()
    {
        var response = await Finance().PostAsync("/api/v1/remittances/820?fileName=sample.820", Edi(Sample820()));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batch = body.RootElement.GetProperty("batches")[0];
        batch.GetProperty("appliedAmount").GetDecimal().Should().Be(1700.00m);
        batch.GetProperty("unappliedCreditAmount").GetDecimal().Should().Be(200.00m);
        batch.GetProperty("exceptionAmount").GetDecimal().Should().Be(100.00m);
        batch.GetProperty("processedBy").GetString().Should().Be(User);

        var invoices = await Finance().GetFromJsonAsync<JsonElement>("/api/v1/premium-invoices/sponsor/GRP001");
        invoices.EnumerateArray().Select(i => i.GetProperty("balanceDue").GetDecimal()).Should().AllBeEquivalentTo(0m);

        var account = await Finance().GetFromJsonAsync<JsonElement>("/api/v1/sponsor-accounts/GRP001");
        account.GetProperty("unappliedCredit").GetDecimal().Should().Be(200.00m);
        account.GetProperty("netBalance").GetDecimal().Should().Be(-200.00m);

        var queue = await Finance().GetFromJsonAsync<JsonElement>("/api/v1/remittances/exceptions");
        queue.GetArrayLength().Should().Be(1);
        queue[0].GetProperty("reference").GetString().Should().Be("INV-UNKNOWN");
    }

    [Fact]
    public async Task SecondUploadOfTheSameFile_IsReportedAsDuplicate()
    {
        await Finance().PostAsync("/api/v1/remittances/820", Edi(Sample820()));

        var response = await Finance().PostAsync("/api/v1/remittances/820", Edi(Sample820()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("batches").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("duplicates")[0].GetProperty("traceNumber").GetString().Should().Be("HTTP-1");
        var account = await Finance().GetFromJsonAsync<JsonElement>("/api/v1/sponsor-accounts/GRP001");
        account.GetProperty("unappliedCredit").GetDecimal().Should().Be(200.00m);
    }

    [Fact]
    public async Task UnreadableFile_Is400_AndPostsNothing()
    {
        var response = await Finance().PostAsync("/api/v1/remittances/820", Edi(Sample820().Replace("TRN*1*HTTP-1", "TRN*1*")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Finance().GetFromJsonAsync<JsonElement>("/api/v1/remittances")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostingCash_NeedsFinanceWrite()
    {
        var clerk = WithPermissions("billing:read", "billing:run");

        (await clerk.PostAsync("/api/v1/remittances/820", Edi(Sample820()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clerk.PostAsync("/api/v1/remittances/lockbox", new StringContent("x", Encoding.UTF8, "text/csv")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clerk.PostAsJsonAsync("/api/v1/remittances/exceptions/any/resolve", new { action = "Dismiss", note = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clerk.GetAsync("/api/v1/remittances/exceptions")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RefreshingAnUnknownGroup_Is404()
    {
        (await Finance().PostAsync("/api/v1/sponsor-accounts/GRP-TYPO/refresh", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Finance().PostAsync("/api/v1/sponsor-accounts/GRP001/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lockbox_UploadAppliesChecks()
    {
        const string csv = "batch,item,deposit_date,check_number,payer_id,check_amount,invoice_number,amount\n" +
                           "009,1,2026-03-06,30001,EMPLOYER-A,1200.00,INV-GRP001-2026-03,1200.00\n";

        var response = await Finance().PostAsync("/api/v1/remittances/lockbox", new StringContent(csv, Encoding.UTF8, "text/csv"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var account = await Finance().GetFromJsonAsync<JsonElement>("/api/v1/sponsor-accounts/GRP001");
        account.GetProperty("openInvoiceBalance").GetDecimal().Should().Be(500.00m);
    }
}
