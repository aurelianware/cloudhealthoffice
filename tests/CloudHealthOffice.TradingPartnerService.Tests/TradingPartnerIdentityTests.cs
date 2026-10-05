using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;
using CloudHealthOffice.TradingPartnerService.Tests.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace CloudHealthOffice.TradingPartnerService.Tests;

/// <summary>
/// Record ids cannot collide across tenants, and a duplicate create is a 409.
/// </summary>
public class TradingPartnerIdTests
{
    [Fact]
    public void Ids_ForTriplesThatCollidedBefore_AreDistinct()
    {
        // Old scheme: both were "x-a-b-prod".
        $"{"x"}-{"a-b"}-{"prod"}".Should().Be($"{"x-a"}-{"b"}-{"prod"}");

        TradingPartnerIds.For("a-b", "x", "prod").Should().NotBe(TradingPartnerIds.For("b", "x-a", "prod"));
        TradingPartnerIds.For("t", "p.q", "e").Should().NotBe(TradingPartnerIds.For("t", "p", "q.e"));
        TradingPartnerIds.For("t", "p", "e").Should().Be(TradingPartnerIds.For("t", "p", "e"));
        TradingPartnerIds.For("t/1", "p?#", "e\\").Should().MatchRegex("^tp\\.[A-Za-z0-9_.-]+$");
    }
}

public class TradingPartnerDuplicateCreateTests : IClassFixture<TradingPartnerPipelineFactory>
{
    private readonly TradingPartnerPipelineFactory _factory;

    public TradingPartnerDuplicateCreateTests(TradingPartnerPipelineFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private HttpClient Admin(string tenant)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("admin", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static object Body(string partnerId) => new
    {
        tradingPartnerId = partnerId,
        environment = "prod",
        partnerName = "Partner " + partnerId,
        partnerType = "Clearinghouse",
    };

    [Fact]
    public async Task DuplicateCreate_Is409_NotA500()
    {
        (await Admin("tenant-1").PostAsJsonAsync("/api/TradingPartners", Body("availity"))).StatusCode
            .Should().Be(HttpStatusCode.Created);

        var second = await Admin("tenant-1").PostAsJsonAsync("/api/TradingPartners", Body("availity"));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Repository.All.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateOfARecordWithTheOldId_Is409()
    {
        _factory.Repository.Seed(new TradingPartner
        {
            Id = "availity-tenant-1-prod", TenantId = "tenant-1", TradingPartnerId = "availity", Environment = "prod"
        });

        var response = await Admin("tenant-1").PostAsJsonAsync("/api/TradingPartners", Body("availity"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}

/// <summary>Against a real mongod: the collection's _id is global, so the old ids collided across tenants.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TradingPartnerMongoIdentityTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private TradingPartnerRepositoryMongo _repository = null!;

    public TradingPartnerMongoIdentityTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("trading_partner_ids");
        _repository = new TradingPartnerRepositoryMongo(_database, NullLogger<TradingPartnerRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static TradingPartner Partner(string tenant, string partnerId, string? id = null) => new()
    {
        Id = id ?? TradingPartnerIds.For(tenant, partnerId, "prod"),
        TenantId = tenant,
        TradingPartnerId = partnerId,
        Environment = "prod",
        PartnerName = partnerId + "@" + tenant,
    };

    [Fact]
    public async Task TwoTenants_WhoseOldIdsCollided_BothCreate_AndEachReadsItsOwn()
    {
        await _repository.CreateAsync(Partner("a-b", "x"));
        await _repository.CreateAsync(Partner("b", "x-a"));

        (await _repository.GetAsync("a-b", "x", "prod"))!.PartnerName.Should().Be("x@a-b");
        (await _repository.GetAsync("b", "x-a", "prod"))!.PartnerName.Should().Be("x-a@b");
    }

    [Fact]
    public async Task DuplicateCreate_ThrowsDuplicate_WhateverTheId()
    {
        await _repository.CreateAsync(Partner("t1", "availity"));

        await FluentActions.Awaiting(() => _repository.CreateAsync(Partner("t1", "availity")))
            .Should().ThrowAsync<DuplicateTradingPartnerException>();
        // Same triple under another id (the unique field index catches it).
        await FluentActions.Awaiting(() => _repository.CreateAsync(Partner("t1", "availity", id: "availity-t1-prod")))
            .Should().ThrowAsync<DuplicateTradingPartnerException>();
    }

    [Fact]
    public async Task RecordSavedWithTheOldId_IsFound_Updated_AndDeleted()
    {
        await _database.GetCollection<TradingPartner>("TradingPartners").InsertOneAsync(Partner("t1", "legacy", id: "legacy-t1-prod"));

        var found = await _repository.GetAsync("t1", "legacy", "prod");
        found!.Id.Should().Be("legacy-t1-prod");

        found.PartnerName = "renamed";
        await _repository.UpdateAsync(found);
        (await _repository.GetAsync("t1", "legacy", "prod"))!.PartnerName.Should().Be("renamed");

        await _repository.DeleteAsync(found.Id, "t1");
        (await _repository.GetAsync("t1", "legacy", "prod")).Should().BeNull();
    }

    [Fact]
    public async Task ATenantNeverReadsAnotherTenantsPartner()
    {
        await _repository.CreateAsync(Partner("t1", "shared"));

        (await _repository.GetAsync("t2", "shared", "prod")).Should().BeNull();
    }
}
