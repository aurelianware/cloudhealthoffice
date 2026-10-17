using System.Net;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PaymentService.Tests.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>A payee's payments by NPI (the provider record's Payments tab), against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class PaymentsByPayeeMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _db = null!;

    public PaymentsByPayeeMongoTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _db = _mongo.CreateDatabase("payments_by_payee");
        var payments = _db.GetCollection<Payment>("Payments");
        await payments.InsertManyAsync(
        [
            new Payment { Id = "p-old", TenantId = "t1", PayeeNPI = "1234567893", CheckNumber = "EFT1", PaymentDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Payment { Id = "p-new", TenantId = "t1", PayeeNPI = "1234567893", CheckNumber = "EFT2", PaymentDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Payment { Id = "p-otherpayee", TenantId = "t1", PayeeNPI = "1999999992", CheckNumber = "EFT3", PaymentDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) },
            new Payment { Id = "p-othertenant", TenantId = "t2", PayeeNPI = "1234567893", CheckNumber = "EFT4", PaymentDate = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) },
        ]);
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private PaymentRepositoryMongo Repo()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = "t1";
        return new PaymentRepositoryMongo(_db, new HttpContextAccessor { HttpContext = ctx }, NullLogger<PaymentRepositoryMongo>.Instance);
    }

    [Fact]
    public async Task Returns_the_payees_payments_in_the_tenant_newest_first()
    {
        var found = await Repo().GetByPayeeNpiAsync("1234567893", 50);

        Assert.Equal(new[] { "p-new", "p-old" }, found.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task Respects_the_limit()
    {
        var found = await Repo().GetByPayeeNpiAsync("1234567893", 1);

        Assert.Equal("p-new", Assert.Single(found).Id);
    }
}

/// <summary>The payee endpoint through the real pipeline: payments:read, a 10-digit NPI.</summary>
public class PaymentsByPayeeEndpointTests : IClassFixture<PaymentPipelineFactory>
{
    private readonly PaymentPipelineFactory _f;

    public PaymentsByPayeeEndpointTests(PaymentPipelineFactory factory)
    {
        _f = factory;
        _f.Payments.GetByPayeeNpiAsync("1234567893", 100).Returns(new List<Payment> { new() { Id = "p1", PayeeNPI = "1234567893" } });
    }

    private HttpClient As(string role)
    {
        var client = _f.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");
        return client;
    }

    [Fact]
    public async Task Finance_reads_a_payees_payments()
    {
        var response = await As(ChoRolePermissions.Finance).GetAsync("/api/payments/payee/1234567893");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("p1", Assert.Single(doc.RootElement.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Without_payments_read_it_is_refused() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await As(ChoRolePermissions.ClaimsExaminer).GetAsync("/api/payments/payee/1234567893")).StatusCode);

    [Fact]
    public async Task A_malformed_npi_is_rejected() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await As(ChoRolePermissions.Finance).GetAsync("/api/payments/payee/12345")).StatusCode);
}
