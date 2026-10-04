using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TenantService.Tests.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// The <c>*Platform</c> blocks services read from <c>GET /tenants/{id}</c>
/// (provider-service reads <c>providerPlatform</c>, claims-service
/// <c>claimsPlatform</c>, idcard-service <c>idCardPlatform</c>) must survive a
/// settings write, storage in MongoDB and a read with the service's own token.
/// Before these properties existed the blocks were dropped on deserialization,
/// so those services always resolved the default platform.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TenantPlatformSettingsRoundTripTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoBackedTenantServiceFactory _factory = null!;

    static TenantPlatformSettingsRoundTripTests()
    {
        // The same convention tenant-service registers at startup.
        ConventionRegistry.Register("CamelCase",
            new ConventionPack { new CamelCaseElementNameConvention() }, _ => true);
    }

    public TenantPlatformSettingsRoundTripTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("tenant_platforms");
        _factory = new MongoBackedTenantServiceFactory(_database);
        _factory.ResetMocks();
        await new TenantRepository(_database, NullLogger<TenantRepository>.Instance)
            .CreateAsync(TenantServiceFactory.NewTenant(TenantServiceFactory.TenantA));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _mongo.DropDatabaseAsync(_database);
    }

    private static object SettingsBody() => new
    {
        configuration = new
        {
            providerPlatform = new
            {
                platform = "qnxt",
                apiEndpoint = "https://qnxt.example/provider",
                platformSettings = new Dictionary<string, string> { ["qnxt:planCode"] = "FL-01" },
            },
            claimsPlatform = new { platform = "facets", platformSettings = new Dictionary<string, string> { ["facets:region"] = "south" } },
            idCardPlatform = new { platform = "vendor" },
            benefitPlanPlatform = new { platform = "healthedge" },
            eligibilityPlatform = new { platform = "availity" },
        },
    };

    [Fact]
    public async Task ProviderPlatform_SetBySettingsManager_IsStoredAndReturnedToProviderService()
    {
        var admin = _factory.UserClient(TenantServiceFactory.TenantA, "admin-1", ChoRolePermissions.TenantAdmin);

        var put = await admin.PutAsJsonAsync("/api/v1/tenants/tenant-a", SettingsBody());
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Stored in MongoDB under the camelCase names services read.
        var stored = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(new BsonDocument("tenantId", TenantServiceFactory.TenantA)).SingleAsync();
        var storedConfig = stored["configuration"].AsBsonDocument;
        storedConfig["providerPlatform"]["platform"].AsString.Should().Be("qnxt");
        storedConfig["providerPlatform"]["platformSettings"]["qnxt:planCode"].AsString.Should().Be("FL-01");
        storedConfig["claimsPlatform"]["platform"].AsString.Should().Be("facets");
        storedConfig["idCardPlatform"]["platform"].AsString.Should().Be("vendor");

        // Read back the way provider-service reads it: a service token for the tenant.
        var asProviderService = _factory.ServiceClient("provider-service", TenantServiceFactory.TenantA);
        var get = await asProviderService.GetAsync("/api/v1/tenants/tenant-a");
        get.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var config = doc.RootElement.GetProperty("configuration");
        var provider = config.GetProperty("providerPlatform");
        provider.GetProperty("platform").GetString().Should().Be("qnxt");
        provider.GetProperty("apiEndpoint").GetString().Should().Be("https://qnxt.example/provider");
        provider.GetProperty("platformSettings").GetProperty("qnxt:planCode").GetString().Should().Be("FL-01");
        config.GetProperty("claimsPlatform").GetProperty("platform").GetString().Should().Be("facets");
        config.GetProperty("claimsPlatform").GetProperty("platformSettings").GetProperty("facets:region").GetString().Should().Be("south");
        config.GetProperty("idCardPlatform").GetProperty("platform").GetString().Should().Be("vendor");
        config.GetProperty("benefitPlanPlatform").GetProperty("platform").GetString().Should().Be("healthedge");
        config.GetProperty("eligibilityPlatform").GetProperty("platform").GetString().Should().Be("availity");
    }

    [Fact]
    public async Task ProviderPlatform_CannotBeSetWithoutSettingsManage()
    {
        var examiner = _factory.UserClient(TenantServiceFactory.TenantA, "examiner-1", ChoRolePermissions.ClaimsExaminer);

        var put = await examiner.PutAsJsonAsync("/api/v1/tenants/tenant-a", SettingsBody());

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stored = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(new BsonDocument("tenantId", TenantServiceFactory.TenantA)).SingleAsync();
        var written = stored["configuration"].AsBsonDocument.TryGetValue("providerPlatform", out var block)
                      && !block.IsBsonNull;
        written.Should().BeFalse();
    }

    [Fact]
    public async Task PlatformBlocks_OnCreate_AreKept()
    {
        var platformAdmin = _factory.UserClient(TenantServiceFactory.TenantA, "ops-1", ChoRolePermissions.PlatformAdmin);

        var create = await platformAdmin.PostAsJsonAsync("/api/v1/tenants", new
        {
            tenantName = "New Plan",
            organizationName = "New Plan Org",
            subscriptionTier = "starter",
            contactInfo = new { email = "admin@new.example" },
            providerPlatform = new { platform = "facets" },
            claimsPlatform = new { platform = "qnxt" },
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var config = doc.RootElement.GetProperty("configuration");
        config.GetProperty("providerPlatform").GetProperty("platform").GetString().Should().Be("facets");
        config.GetProperty("claimsPlatform").GetProperty("platform").GetString().Should().Be("qnxt");
    }

    [Fact]
    public async Task PersonalRepresentativeControls_DefaultToSecondPerson_AndTheOverrideRoundTrips()
    {
        var asPersonalRepService = _factory.ServiceClient("personal-representative-service", TenantServiceFactory.TenantA);

        // Never set: the typed default (a second person is required) is returned.
        using (var before = JsonDocument.Parse(await asPersonalRepService.GetStringAsync("/api/v1/tenants/tenant-a")))
        {
            before.RootElement.GetProperty("configuration").GetProperty("personalRepresentativeControls")
                .GetProperty("requireSecondPerson").GetBoolean().Should().BeTrue();
        }

        var admin = _factory.UserClient(TenantServiceFactory.TenantA, "admin-1", ChoRolePermissions.TenantAdmin);
        var put = await admin.PutAsJsonAsync("/api/v1/tenants/tenant-a", new
        {
            configuration = new
            {
                personalRepresentativeControls = new { requireSecondPerson = false },
                paymentControls = new { enforceSeparationOfDuties = true },
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(new BsonDocument("tenantId", TenantServiceFactory.TenantA)).SingleAsync();
        stored["configuration"]["personalRepresentativeControls"]["requireSecondPerson"].AsBoolean.Should().BeFalse();

        using var after = JsonDocument.Parse(await asPersonalRepService.GetStringAsync("/api/v1/tenants/tenant-a"));
        var config = after.RootElement.GetProperty("configuration");
        config.GetProperty("personalRepresentativeControls").GetProperty("requireSecondPerson").GetBoolean().Should().BeFalse();
        config.GetProperty("paymentControls").GetProperty("enforceSeparationOfDuties").GetBoolean().Should().BeTrue();
    }

    private const string Pin = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    private static object NachaBody(object nachaTransmission) => new
    {
        configuration = new { paymentControls = new { enforceSeparationOfDuties = true, nachaTransmission } },
    };

    private static object ValidNacha() => new
    {
        enabled = true,
        host = "sftp.bank.example",
        port = 22,
        username = "cho-plan",
        privateKeySecretRef = "nacha--tenant-a--sftp-key",
        passwordSecretRef = "nacha--tenant-a--sftp-passphrase",
        hostKeyFingerprint = Pin,
        remoteDirectory = "/inbound/ach",
    };

    [Fact]
    public async Task NachaTransmission_RoundTrips_SecretNamesOnlyToServicesAndSettingsManagers()
    {
        var admin = _factory.UserClient(TenantServiceFactory.TenantA, "admin-1", ChoRolePermissions.TenantAdmin);

        var put = await admin.PutAsJsonAsync("/api/v1/tenants/tenant-a", NachaBody(ValidNacha()));
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var stored = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(new BsonDocument("tenantId", TenantServiceFactory.TenantA)).SingleAsync();
        var storedNacha = stored["configuration"]["paymentControls"]["nachaTransmission"].AsBsonDocument;
        storedNacha["hostKeyFingerprint"].AsString.Should().Be(Pin);
        storedNacha["privateKeySecretRef"].AsString.Should().Be("nacha--tenant-a--sftp-key");
        storedNacha.Names.Should().NotContain(new[] { "unknownProperties", "privateKeyConfigured", "passwordConfigured" });

        // premium-billing-service and capitation-service read it with their own service token.
        using (var asService = JsonDocument.Parse(await _factory.ServiceClient("premium-billing-service", TenantServiceFactory.TenantA)
                   .GetStringAsync("/api/v1/tenants/tenant-a")))
        {
            var nacha = asService.RootElement.GetProperty("configuration").GetProperty("paymentControls").GetProperty("nachaTransmission");
            nacha.GetProperty("enabled").GetBoolean().Should().BeTrue();
            nacha.GetProperty("host").GetString().Should().Be("sftp.bank.example");
            nacha.GetProperty("privateKeySecretRef").GetString().Should().Be("nacha--tenant-a--sftp-key");
            nacha.GetProperty("passwordSecretRef").GetString().Should().Be("nacha--tenant-a--sftp-passphrase");
            nacha.GetProperty("hostKeyFingerprint").GetString().Should().Be(Pin);
            nacha.GetProperty("remoteDirectory").GetString().Should().Be("/inbound/ach");
        }

        // A reader without settings:manage sees that credentials are configured, not their names.
        var approver = _factory.UserClient(TenantServiceFactory.TenantA, "approver-1", ChoRolePermissions.FinanceApprover);
        var readerBody = await approver.GetStringAsync("/api/v1/tenants/tenant-a");
        readerBody.Should().NotContain("nacha--tenant-a--");
        using var reader = JsonDocument.Parse(readerBody);
        var masked = reader.RootElement.GetProperty("configuration").GetProperty("paymentControls").GetProperty("nachaTransmission");
        masked.GetProperty("privateKeyConfigured").GetBoolean().Should().BeTrue();
        masked.GetProperty("passwordConfigured").GetBoolean().Should().BeTrue();
        masked.GetProperty("privateKeySecretRef").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("privateKey")]
    public async Task NachaTransmission_WithALiteralCredential_IsRefused_AndNothingIsStored(string field)
    {
        var admin = _factory.UserClient(TenantServiceFactory.TenantA, "admin-1", ChoRolePermissions.TenantAdmin);
        var body = new Dictionary<string, object?>
        {
            ["enabled"] = false, ["host"] = "sftp.bank.example", [field] = "hunter2-literal-secret",
        };

        var put = await admin.PutAsJsonAsync("/api/v1/tenants/tenant-a", NachaBody(body));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().NotContain("hunter2");
        var stored = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(new BsonDocument("tenantId", TenantServiceFactory.TenantA)).SingleAsync();
        stored.ToJson().Should().NotContain("hunter2");
    }

    [Theory]
    [InlineData("privateKeySecretRef", "nacha--tenant-b--key")]      // another tenant's secret
    [InlineData("privateKeySecretRef", "cosmos-primary-key")]        // a platform secret
    [InlineData("hostKeyFingerprint", null)]                         // enabled without a pinned host key
    [InlineData("hostKeyFingerprint", "MD5:aa:bb")]                  // not a SHA-256 pin
    public async Task NachaTransmission_Invalid_IsRefused(string field, string? value)
    {
        var admin = _factory.UserClient(TenantServiceFactory.TenantA, "admin-1", ChoRolePermissions.TenantAdmin);
        var nacha = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(ValidNacha()))!;
        nacha[field] = value;

        var put = await admin.PutAsJsonAsync("/api/v1/tenants/tenant-a", NachaBody(nacha));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The real pipeline over a real <see cref="TenantRepository"/> in MongoDB.</summary>
    private sealed class MongoBackedTenantServiceFactory : TenantServiceFactory
    {
        private readonly IMongoDatabase _database;

        public MongoBackedTenantServiceFactory(IMongoDatabase database) => _database = database;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantRepository>();
                services.AddSingleton<ITenantRepository>(sp =>
                    new TenantRepository(_database, NullLogger<TenantRepository>.Instance));
            });
        }
    }
}
