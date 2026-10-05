using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using ProviderContractsService.Models;

namespace ProviderContractsService.Tests.Security;

/// <summary>
/// The real pipeline and the real <c>MongoProviderContractRepository</c>, with
/// only the Mongo collection mocked. Writes record the token subject as the
/// actor and the token tenant as the tenant, whatever the body says.
/// </summary>
public class ContractsActorFromTokenTests : IClassFixture<ContractsActorFromTokenTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IMongoCollection<ProviderContract>> Collection { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                var database = new Mock<IMongoDatabase>();
                database.Setup(d => d.GetCollection<ProviderContract>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
                    .Returns(() => Collection.Object);
                services.RemoveAll<IMongoDatabase>();
                services.AddSingleton(database.Object);
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "contracts-user-7";
    private readonly Factory _factory;
    private readonly List<ProviderContract> _inserted = new();
    private readonly List<ProviderContract> _replaced = new();

    public ContractsActorFromTokenTests(Factory factory)
    {
        _factory = factory;
        var collection = _factory.Collection;
        collection.Reset();
        collection.Setup(c => c.Indexes).Returns(Mock.Of<IMongoIndexManager<ProviderContract>>());
        collection.Setup(c => c.InsertOneAsync(It.IsAny<ProviderContract>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ProviderContract c, InsertOneOptions _, CancellationToken _) => _inserted.Add(c))
            .Returns(Task.CompletedTask);
        collection.Setup(c => c.ReplaceOneAsync(It.IsAny<FilterDefinition<ProviderContract>>(), It.IsAny<ProviderContract>(),
                It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()))
            .Callback((FilterDefinition<ProviderContract> _, ProviderContract c, ReplaceOptions _, CancellationToken _) => _replaced.Add(c))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(1, 1, null));
    }

    /// <summary>Every Find returns this contract (stored in <paramref name="tenant"/>).</summary>
    private void Existing(string tenant, ProviderContractStatus status = ProviderContractStatus.Draft)
    {
        var existing = new ProviderContract
        {
            Id = "c-1",
            TenantId = tenant,
            ContractNumber = "CTR-1",
            ProviderNPI = "1234567890",
            ProviderType = ProviderType.Individual,
            LineOfBusiness = LineOfBusiness.Commercial,
            PaymentMethodology = PaymentMethodology.FullCapitation,
            Status = status,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "original-user",
            LastUpdatedBy = "original-user"
        };
        _factory.Collection.Setup(c => c.FindAsync(It.IsAny<FilterDefinition<ProviderContract>>(),
                It.IsAny<FindOptions<ProviderContract, ProviderContract>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Cursor(existing));
    }

    private static IAsyncCursor<ProviderContract> Cursor(params ProviderContract[] items)
    {
        var cursor = new Mock<IAsyncCursor<ProviderContract>>();
        cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true).ReturnsAsync(false);
        cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(true).Returns(false);
        cursor.Setup(c => c.Current).Returns(items);
        return cursor.Object;
    }

    private HttpClient Client(string tenant)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, ChoRolePermissions.ProviderRelations));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    [Fact]
    public async Task Create_RecordsTokenActorAndTenant_NotBody()
    {
        // Before: CreatedBy / LastUpdatedBy were whatever the body said (or
        // nothing), and the tenant came from an unauthenticated header.
        var response = await Client(Tenant).PostAsJsonAsync("/api/v1/contracts", new
        {
            tenantId = OtherTenant,
            providerNPI = "1234567890",
            providerType = "Individual",
            lineOfBusiness = "Commercial",
            paymentMethodology = "FullCapitation",
            effectiveDate = "2026-01-01T00:00:00Z",
            createdBy = "someone-else",
            lastUpdatedBy = "someone-else"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var saved = _inserted.Should().ContainSingle().Subject;
        saved.TenantId.Should().Be(Tenant);
        saved.CreatedBy.Should().Be(User);
        saved.LastUpdatedBy.Should().Be(User);
    }

    [Fact]
    public async Task Update_RecordsTokenActor_AndKeepsCreator()
    {
        Existing(Tenant);

        var response = await Client(Tenant).PutAsJsonAsync("/api/v1/contracts/c-1", new
        {
            tenantId = OtherTenant,
            contractNumber = "CTR-1",
            providerNPI = "1234567890",
            providerName = "Dr. Updated",
            providerType = "Individual",
            lineOfBusiness = "Commercial",
            paymentMethodology = "FullCapitation",
            effectiveDate = "2026-01-01T00:00:00Z",
            createdBy = "someone-else",
            lastUpdatedBy = "someone-else"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = _replaced.Should().ContainSingle().Subject;
        saved.TenantId.Should().Be(Tenant);
        saved.CreatedBy.Should().Be("original-user");
        saved.LastUpdatedBy.Should().Be(User);
    }

    [Theory]
    [InlineData("activate", ProviderContractStatus.Draft)]
    [InlineData("reinstate", ProviderContractStatus.Suspended)]
    [InlineData("suspend", ProviderContractStatus.Active)]
    [InlineData("terminate", ProviderContractStatus.Active)]
    public async Task LifecycleChanges_RecordTokenActor(string action, ProviderContractStatus from)
    {
        // Before: activate/suspend/terminate/reinstate never recorded who acted.
        Existing(Tenant, from);

        var response = await Client(Tenant).PutAsJsonAsync($"/api/v1/contracts/c-1/{action}", new { reason = "test" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = _replaced.Should().ContainSingle().Subject;
        saved.LastUpdatedBy.Should().Be(User);
        saved.TenantId.Should().Be(Tenant);
    }
}
