using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// TX-05 through the production create path: the repository applies the
/// Texas Medicaid clock from configuration and stores the result.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TexasMedicaidDecisionClockMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-tx";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("auth_tx_clock");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private AuthorizationRepositoryMongo Repo(bool texas)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        var settings = new Dictionary<string, string?>();
        if (texas)
        {
            settings["PriorAuthDeadlines:TexasMedicaid:Tenants:0"] = Tenant;
            settings["PriorAuthDeadlines:TexasMedicaid:Holidays:0"] = "2026-10-20";
        }
        return new AuthorizationRepositoryMongo(
            _db, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new HttpContextAccessor { HttpContext = ctx },
            NullLogger<AuthorizationRepositoryMongo>.Instance);
    }

    private static Authorization Request(string id) => new()
    {
        Id = id,
        AuthorizationNumber = "AUTH-" + id,
        MemberId = "MBR-001",
        PatientFirstName = "Jane",
        PatientLastName = "Doe",
        PatientDateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LineOfBusiness = LineOfBusiness.Medicaid,
        RequestingProviderNPI = "1234567890",
        AuthorizationType = AuthorizationType.PreAuthorization,
        DecisionScenario = PaDecisionScenario.Hospitalized,
        ServiceTypeCode = "1",
        RequestedServiceDateFrom = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
        // Monday Oct 19, 2026, 10:00 CDT.
        SubmittedDate = new DateTime(2026, 10, 19, 15, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task ConfiguredTenant_StoresTheTexasDeadline()
    {
        await Repo(texas: true).CreateAsync(Request("tx-1"));

        var stored = (await Repo(texas: true).GetByIdAsync("tx-1"))!;
        // One business day, with Tuesday Oct 20 configured as a holiday: Wednesday 10:00 CDT.
        stored.DecisionDueAt.Should().Be(new DateTime(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc));
        stored.DecisionDeadlinePolicy.Should().Be("cho-receipt-v1+tx-gc-540-v1:Hospitalized");
        stored.DecisionScenario.Should().Be(PaDecisionScenario.Hospitalized);
    }

    [Fact]
    public async Task UnconfiguredTenant_KeepsTheFederalDeadline()
    {
        await Repo(texas: false).CreateAsync(Request("tx-2"));

        var stored = (await Repo(texas: false).GetByIdAsync("tx-2"))!;
        stored.DecisionDueAt.Should().Be(new DateTime(2026, 10, 26, 15, 0, 0, DateTimeKind.Utc));
        stored.DecisionDeadlinePolicy.Should().Be("cho-receipt-v1");
    }
}
