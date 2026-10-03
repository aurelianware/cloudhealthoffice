using CloudHealthOffice.Testing.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// The lookups the token service relies on to decide who may enter a tenant,
/// run against a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class IdentityDirectoryTests : IAsyncLifetime
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string OtherTid = "22222222-2222-2222-2222-222222222222";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private IdentityDirectory _directory = null!;

    static IdentityDirectoryTests()
    {
        // The same convention tenant-service registers at startup.
        ConventionRegistry.Register("CamelCase",
            new ConventionPack { new CamelCaseElementNameConvention() }, _ => true);
    }

    public IdentityDirectoryTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("identity");
        _directory = new IdentityDirectory(_database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private Task AddTenantRecord(object record)
        => _database.GetCollection<BsonDocument>("Tenants").InsertOneAsync(record.ToBsonDocument());

    private async Task<TenantUser> AddUser(string tenantId, string email, string oid = "", string tid = "")
    {
        var user = new TenantUser
        {
            TenantId = tenantId,
            Email = email,
            EmailNormalized = email.ToLowerInvariant(),
            AzureAdObjectId = oid,
            AzureAdTenantId = tid,
            Roles = new List<string> { "ClaimsExaminer" },
        };
        await _database.GetCollection<TenantUser>("TenantUsers").InsertOneAsync(user);
        return user;
    }

    [Fact]
    public async Task FindUserByEmail_OneMatch_ReturnsIt()
    {
        var user = await AddUser("t1", "Pat@Acme.com");

        var found = await _directory.FindUserByEmailAsync("t1", " pat@acme.com ", default);

        found!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task FindUserByEmail_TwoRecordsWithTheAddress_ReturnsNeither()
    {
        await AddUser("t1", "pat@acme.com");
        await AddUser("t1", "pat@acme.com");

        var found = await _directory.FindUserByEmailAsync("t1", "pat@acme.com", default);

        found.Should().BeNull("the token service must not link an identity to an arbitrary one of two records");
    }

    [Fact]
    public async Task Tenant_SuspendedRecordWithActiveSubscription_IsNotActive()
    {
        await AddTenantRecord(new { tenantId = "t1", tenantName = "Acme", status = "suspended" });
        await AddTenantRecord(new { tenantId = "t1", organizationName = "Acme", azureTenantId = Tid, subscriptionStatus = "Active" });

        var tenant = await _directory.GetTenantAsync("t1", default);
        var byDirectory = await _directory.GetTenantsAsync(Tid, default);
        var all = await _directory.GetTenantsAsync(null, default);

        tenant!.IsActive.Should().BeFalse();
        byDirectory.Should().ContainSingle().Which.IsActive.Should().BeFalse();
        all.Should().ContainSingle().Which.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Tenant_AllRecordsActive_IsActiveWithTheSubscriptionsDirectory()
    {
        await AddTenantRecord(new { tenantId = "t1", tenantName = "Acme", status = "active" });
        await AddTenantRecord(new { tenantId = "t1", organizationName = "Acme", azureTenantId = Tid, subscriptionStatus = "Trial" });

        var tenant = await _directory.GetTenantAsync("t1", default);

        tenant!.IsActive.Should().BeTrue();
        tenant.AzureTenantId.Should().Be(Tid);
        tenant.TenantName.Should().Be("Acme");
    }

    [Fact]
    public async Task Tenant_RecordsNamingDifferentDirectories_IsNotActiveAndHasNoDirectory()
    {
        await AddTenantRecord(new { tenantId = "t1", azureTenantId = Tid, subscriptionStatus = "Active" });
        await AddTenantRecord(new { tenantId = "t1", azureTenantId = OtherTid, subscriptionStatus = "Active" });

        var tenant = await _directory.GetTenantAsync("t1", default);

        tenant!.IsActive.Should().BeFalse();
        tenant.AzureTenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task Memberships_CarryTheCombinedTenantStatus()
    {
        await AddTenantRecord(new { tenantId = "t1", status = "suspended" });
        await AddTenantRecord(new { tenantId = "t1", azureTenantId = Tid, subscriptionStatus = "Active" });
        await AddUser("t1", "pat@acme.com", oid: "oid-1", tid: Tid);

        var memberships = await _directory.GetMembershipsAsync(Tid, "oid-1", default);

        memberships.Should().ContainSingle().Which.Tenant!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Link_UnlinkedUser_RecordsOidAndTid()
    {
        var user = await AddUser("t1", "pat@acme.com");

        var (outcome, linked) = await _directory.LinkEntraIdentityAsync("t1", user.Id, "oid-1", Tid, default);

        outcome.Should().Be(LinkOutcome.Linked);
        linked!.AzureAdObjectId.Should().Be("oid-1");
        linked.AzureAdTenantId.Should().Be(Tid);
    }

    [Fact]
    public async Task Link_UserLinkedToAnotherIdentity_IsAConflictAndUnchanged()
    {
        var user = await AddUser("t1", "pat@acme.com", oid: "oid-original", tid: Tid);

        var (outcome, _) = await _directory.LinkEntraIdentityAsync("t1", user.Id, "oid-attacker", OtherTid, default);
        var stored = await _directory.FindUserByEmailAsync("t1", "pat@acme.com", default);

        outcome.Should().Be(LinkOutcome.Conflict);
        stored!.AzureAdObjectId.Should().Be("oid-original");
        stored.AzureAdTenantId.Should().Be(Tid);
    }

    [Fact]
    public async Task Link_LegacyOidOnlyLink_RecordsTheDirectory()
    {
        var user = await AddUser("t1", "pat@acme.com", oid: "oid-1");

        var (outcome, linked) = await _directory.LinkEntraIdentityAsync("t1", user.Id, "oid-1", Tid, default);

        outcome.Should().Be(LinkOutcome.Linked);
        linked!.AzureAdTenantId.Should().Be(Tid);
    }

    [Theory]
    [InlineData("Invited")]
    [InlineData("Disabled")]
    [InlineData("Locked")]
    public async Task Link_UnlinkedUserThatIsNotActive_IsAConflictAndUnchanged(string status)
    {
        // An Invited user is linked only by redeeming its invitation, never by
        // a first sign-in with a matching address.
        var user = await AddUser("t1", "pat@acme.com");
        await _database.GetCollection<TenantUser>("TenantUsers").UpdateOneAsync(
            u => u.Id == user.Id, Builders<TenantUser>.Update.Set(u => u.Status, status));

        var (outcome, _) = await _directory.LinkEntraIdentityAsync("t1", user.Id, "oid-1", Tid, default);
        var stored = await _directory.FindUserByEmailAsync("t1", "pat@acme.com", default);

        outcome.Should().Be(LinkOutcome.Conflict);
        stored!.AzureAdObjectId.Should().BeEmpty();
        stored.Status.Should().Be(status);
    }

    [Fact]
    public async Task Link_UserInAnotherTenant_IsNotFound()
    {
        var user = await AddUser("t1", "pat@acme.com");

        var (outcome, _) = await _directory.LinkEntraIdentityAsync("t2", user.Id, "oid-1", Tid, default);

        outcome.Should().Be(LinkOutcome.NotFound);
    }
}
