using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CHO.TerminologyService.Controllers;
using CHO.TerminologyService.Data;
using CHO.TerminologyService.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests.Security;

/// <summary>
/// TMPPM rules through terminology-service's API (the portal and the
/// ingestion tool no longer open its database): reads need terminology:read,
/// writes of the shared rules/editions/diffs need platform:admin, and
/// published overrides go to the token tenant only.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TmppmApiTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private WebApplicationFactory<TerminologyController> _factory = null!;

    public TmppmApiTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("terminology_tmppm");
        var databaseName = _database.DatabaseNamespace.DatabaseName;
        _factory = new WebApplicationFactory<TerminologyController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("TerminologyService:MongoConnectionString", _mongo.ConnectionString);
            builder.UseSetting("TerminologyService:MongoDatabaseName", databaseName);
            builder.UseSetting("TerminologyService:AutoLoadMaps:0:FilePath", "/nonexistent/terminology-test-map.txt");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _mongo.DropDatabaseAsync(_database);
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("tmppm-user", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static TmppmPaRule Rule(string id, string category, string ruleType, params string[] codes) => new()
    {
        RuleId = id,
        State = "TX",
        Category = category,
        TmppmRef = "§" + id,
        RuleType = ruleType,
        ProcedureCodes = codes.ToList(),
        CodeSystem = "CPT",
        AuthRequired = true,
        EffectiveDate = new DateOnly(2026, 4, 1),
        SourceEdition = "2026-04",
    };

    private async Task SeedAsync()
    {
        var put = await Client(Tenant, ChoRolePermissions.PlatformAdmin).PutAsJsonAsync("/api/v1/tmppm/rules", new[]
        {
            Rule("TX-1", "Home Health", "AuthRequired", "99500", "99501"),
            Rule("TX-2", "Therapy", "UnitLimit", "97110"),
        }, Json);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var edition = await Client(Tenant, ChoRolePermissions.PlatformAdmin).PutAsJsonAsync("/api/v1/tmppm/editions/2026-04",
            new TmppmEdition { EditionId = "ignored", IngestedAt = DateTime.UtcNow, SourceUrl = "https://www.tmhp.com" }, Json);
        edition.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reads_NeedTerminologyRead_AndReturnTheRules()
    {
        await SeedAsync();
        var reader = Client(Tenant, ChoRolePermissions.UMCoordinator);

        var byCode = await reader.GetFromJsonAsync<List<TmppmPaRule>>("/api/v1/tmppm/rules?code=99500&state=TX", Json);
        byCode!.Select(r => r.RuleId).Should().Equal("TX-1");

        var categories = await reader.GetFromJsonAsync<List<TmppmCategoryGroup>>("/api/v1/tmppm/categories?state=TX", Json);
        categories!.Select(g => g.Priority).Should().Equal("P1", "P2");

        (await reader.GetFromJsonAsync<List<string>>("/api/v1/tmppm/codes?prefix=995", Json))!.Should().Equal("99500", "99501");
        (await reader.GetFromJsonAsync<TmppmEdition>("/api/v1/tmppm/editions/current", Json))!.EditionId.Should().Be("2026-04");

        (await _factory.CreateClient().GetAsync("/api/v1/tmppm/rules?code=99500")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/tmppm/rules")]
    [InlineData("PUT", "/api/v1/tmppm/editions/2026-05")]
    [InlineData("POST", "/api/v1/tmppm/diffs")]
    public async Task SharedWrites_TenantAdmin_Is403(string method, string path)
    {
        // TenantAdmin holds *:*, which never satisfies a platform permission.
        object body = path.EndsWith("rules")
            ? new[] { Rule("TX-9", "X", "AuthRequired", "11111") }
            : path.Contains("diffs") ? new TmppmDiffReport { FromEdition = "a", ToEdition = "b" } : new TmppmEdition();
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(body, options: Json) };

        var response = await Client(Tenant, ChoRolePermissions.TenantAdmin).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _database.GetCollection<TmppmPaRule>(MongoTmppmStore.RulesCollection).CountDocumentsAsync(FilterDefinition<TmppmPaRule>.Empty))
            .Should().Be(0);
    }

    [Fact]
    public async Task PublishOverrides_GoToTheTokenTenantOnly_AndTwoTenantsDoNotCollide()
    {
        var rules = new[] { Rule("TX-1", "Home Health", "AuthRequired", "99500") };

        var a = await Client(Tenant, ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/v1/tmppm/overrides",
            new TmppmPublishOverridesRequest { EditionId = "2026-04", Rules = rules.ToList() }, Json);
        var b = await Client(OtherTenant, ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/v1/tmppm/overrides",
            new TmppmPublishOverridesRequest { EditionId = "2026-04", Rules = rules.ToList() }, Json);

        a.StatusCode.Should().Be(HttpStatusCode.OK);
        b.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await _database.GetCollection<ConceptMapEntry>("concept_map_entries")
            .Find(FilterDefinition<ConceptMapEntry>.Empty).ToListAsync();
        entries.Should().HaveCount(2);
        entries.Select(e => e.TenantId).Should().BeEquivalentTo(Tenant, OtherTenant);
        entries.Select(e => e.Id).Should().OnlyHaveUniqueItems();

        // The tenant's own translation sees it; the version is the tenant's.
        var versions = await _database.GetCollection<MapVersion>("map_versions").Find(FilterDefinition<MapVersion>.Empty).ToListAsync();
        versions.Select(v => v.TenantId).Should().BeEquivalentTo(Tenant, OtherTenant);
    }

    [Fact]
    public async Task PublishOverrides_WithoutSettingsManage_Is403()
    {
        var response = await Client(Tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync("/api/v1/tmppm/overrides",
            new TmppmPublishOverridesRequest { EditionId = "2026-04", Rules = [Rule("TX-1", "Home Health", "AuthRequired", "99500")] }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
