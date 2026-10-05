using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CHO.TerminologyService.Controllers;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests.Security;

/// <summary>
/// The real terminology-service pipeline against the shared EphemeralMongo.
/// Every caller except /health needs a CHO token and the tenant comes from it.
/// Global crosswalk maps are shared by every tenant: loading one needs
/// platform:admin. Plan overrides belong to the token tenant only.
///
/// All data is seeded through the API so the same tests run unchanged against
/// the service before the migration (they fail there) and after it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TerminologyPipelineAuthTests : IAsyncLifetime
{
    private const string Snomed = "http://snomed.info/sct";
    private const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Subject = "terminology-user-7";

    private const string GlobalMap = "NLM-SNOMED-ICD10CM";
    private const string OtherTenantOverrideMap = "Tenant2-Secret-Overrides";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private WebApplicationFactory<TerminologyController> _factory = null!;

    public TerminologyPipelineAuthTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("terminology_pipeline");
        var databaseName = _database.DatabaseNamespace.DatabaseName;
        _factory = new WebApplicationFactory<TerminologyController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Program.cs reads these while building, so they go in as host settings.
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

    // ── Clients ─────────────────────────────────────────────────────────

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(Subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient ServiceClient(string tenant, string clientId = "benefit-plan-service")
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }

    private HttpClient PlatformAdmin() => Client(Tenant, ChoRolePermissions.PlatformAdmin);

    // ── Seeding through the API ────────────────────────────────────────

    private static string LoadPath(string mapName, string version, bool isOverride, string? tenantId, string format = "CSV")
        => $"/admin/maps/load?format={format}&mapName={mapName}&version={version}" +
           $"&sourceSystem={Uri.EscapeDataString(Snomed)}&targetSystem={Uri.EscapeDataString(Icd10Cm)}" +
           (isOverride ? "&isOverride=true" : string.Empty) +
           (tenantId is null ? string.Empty : $"&tenantId={tenantId}");

    private static StringContent Csv(string sourceCode, string targetCode, string targetDisplay)
        => new($"source_code,source_display,target_code,target_display\n{sourceCode},Source {sourceCode},{targetCode},{targetDisplay}\n",
            Encoding.UTF8, "text/csv");

    private static StringContent Rf2(string sourceCode, string targetCode)
        => new("id\teffectiveTime\tactive\tmoduleId\trefsetId\treferencedComponentId\tmapGroup\tmapPriority\tmapRule\tmapAdvice\tmapTarget\tcorrelationId\tmapCategoryId\n" +
               $"row-1\t20260301\t1\t731000124108\t6011000124106\t{sourceCode}\t1\t1\tTRUE\tALWAYS {targetCode}\t{targetCode}\t447561005\t447637006\n",
            Encoding.UTF8, "text/plain");

    private async Task SeedGlobalMapAsync(string sourceCode = "1001", string targetCode = "A00.0")
    {
        using var response = await PlatformAdmin().PostAsync(
            LoadPath(GlobalMap, "202603", isOverride: false, tenantId: null),
            Csv(sourceCode, targetCode, "Global display"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A tenant's own admin loads its plan overrides, naming its own tenant.</summary>
    private async Task SeedOverrideAsync(string tenant, string mapName, string sourceCode, string targetCode)
    {
        using var response = await Client(tenant, ChoRolePermissions.TenantAdmin).PostAsync(
            LoadPath(mapName, "2026Q1", isOverride: true, tenantId: tenant),
            Csv(sourceCode, targetCode, $"Override for {tenant}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string TranslatePath(string code, string? tenantId = null)
        => $"/fhir/ConceptMap/$translate?system={Uri.EscapeDataString(Snomed)}&code={code}" +
           $"&target={Uri.EscapeDataString(Icd10Cm)}" + (tenantId is null ? string.Empty : $"&tenantId={tenantId}");

    private static object TranslateBody(string code, string? tenantId)
        => new { system = Snomed, code, targetSystem = Icd10Cm, tenantId };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    private static List<string> MatchCodes(JsonElement translateResponse)
        => translateResponse.GetProperty("matches").EnumerateArray()
            .Select(m => m.GetProperty("concept").GetProperty("code").GetString()!)
            .ToList();

    private async Task<List<BsonDocument>> MapVersionsAsync()
        => await _database.GetCollection<BsonDocument>("map_versions").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

    private async Task<List<BsonDocument>> EntriesAsync()
        => await _database.GetCollection<BsonDocument>("concept_map_entries").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

    // ── Authentication ──────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/fhir/ConceptMap/$translate?system=http%3A%2F%2Fsnomed.info%2Fsct&code=1001&target=http%3A%2F%2Fhl7.org%2Ffhir%2Fsid%2Ficd-10-cm")]
    [InlineData("GET", "/fhir/CodeSystem/$lookup?system=http%3A%2F%2Fhl7.org%2Ffhir%2Fsid%2Ficd-10-cm&code=A00.0")]
    [InlineData("GET", "/admin/maps")]
    [InlineData("POST", "/fhir/ConceptMap/$translate")]
    [InlineData("POST", "/fhir/ConceptMap/$batch-translate")]
    public async Task NoToken_IsRejected(string method, string path)
    {
        var client = _factory.CreateClient();
        // A tenant header alone is not a credential.
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = path.EndsWith("batch-translate", StringComparison.Ordinal)
                ? JsonContent.Create(new[] { TranslateBody("1001", null) })
                : JsonContent.Create(TranslateBody("1001", null));
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_CannotLoadAGlobalMap()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        using var response = await client.PostAsync(
            LoadPath(GlobalMap, "202699", isOverride: false, tenantId: null), Csv("1001", "Z99.9", "Injected"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await MapVersionsAsync());
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Health_IsAnonymous_AndListsNoTenantOverrideMaps()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(GlobalMap, body);
        Assert.DoesNotContain(OtherTenantOverrideMap, body);
    }

    // ── Reads: permission ───────────────────────────────────────────────

    [Fact]
    public async Task PostTranslate_IsARead_ClaimsExaminerWithTerminologyReadIsAllowed()
    {
        await SeedGlobalMapAsync();

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .PostAsJsonAsync("/fhir/ConceptMap/$translate", TranslateBody("1001", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["A00.0"], MatchCodes(await ReadJsonAsync(response)));
    }

    [Fact]
    public async Task Translate_WithoutTerminologyRead_IsForbidden()
    {
        // MemberServices holds no terminology:read.
        var client = Client(Tenant, ChoRolePermissions.MemberServices);

        using var get = await client.GetAsync(TranslatePath("1001"));
        using var post = await client.PostAsJsonAsync("/fhir/ConceptMap/$batch-translate", new[] { TranslateBody("1001", null) });

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task BatchTranslate_ServiceToken_GetsItsTenantsOverrides()
    {
        // benefit-plan-service's crosswalk: a service token for the tenant, body tenant equal to it.
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(Tenant, "Tenant1-Overrides", "1001", "T11.1");

        using var response = await ServiceClient(Tenant)
            .PostAsJsonAsync("/fhir/ConceptMap/$batch-translate", new[] { TranslateBody("1001", Tenant) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await ReadJsonAsync(response);
        Assert.Contains("T11.1", MatchCodes(results[0]));
    }

    // ── Reads: tenant comes from the token ─────────────────────────────

    [Fact]
    public async Task Translate_UsesTheTokenTenantsOverrides_WithoutATenantParameter()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(Tenant, "Tenant1-Overrides", "1001", "T11.1");

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync(TranslatePath("1001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var codes = MatchCodes(await ReadJsonAsync(response));
        Assert.Equal("T11.1", codes[0]);
    }

    [Fact]
    public async Task Translate_QueryTenantOtherThanToken_IsForbidden_AndLeaksNoOverride()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .GetAsync(TranslatePath("1001", tenantId: OtherTenant));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("B99.9", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostTranslate_BodyTenantOtherThanToken_IsForbidden()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .PostAsJsonAsync("/fhir/ConceptMap/$translate", TranslateBody("1001", OtherTenant));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("B99.9", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BatchTranslate_BodyTenantOtherThanToken_IsForbidden()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await ServiceClient(Tenant)
            .PostAsJsonAsync("/fhir/ConceptMap/$batch-translate",
                new[] { TranslateBody("1001", Tenant), TranslateBody("1001", OtherTenant) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("B99.9", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Lookup_QueryTenantOtherThanToken_IsForbidden()
    {
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await ServiceClient(Tenant, "claims-service").GetAsync(
            $"/fhir/CodeSystem/$lookup?system={Uri.EscapeDataString(Icd10Cm)}&code=B99.9&tenantId={OtherTenant}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Override for", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Lookup_UsesTheTokenTenantsOverrideDisplay()
    {
        await SeedOverrideAsync(Tenant, "Tenant1-Overrides", "1001", "T11.1");

        using var response = await ServiceClient(Tenant, "claims-service").GetAsync(
            $"/fhir/CodeSystem/$lookup?system={Uri.EscapeDataString(Icd10Cm)}&code=T11.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("result").GetBoolean());
        Assert.Equal($"Override for {Tenant}", body.GetProperty("display").GetString());
    }

    [Fact]
    public async Task Translate_ReportsTheGlobalMapVersion_NeverAnotherTenantsOverrideVersion()
    {
        await SeedGlobalMapAsync();
        // Loaded after the global map, so it is the newest active version for the pair.
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync(TranslatePath("1001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mapVersionId = (await ReadJsonAsync(response)).GetProperty("mapVersionId").GetString();
        Assert.StartsWith(GlobalMap, mapVersionId);
    }

    [Fact]
    public async Task MapVersions_ListsGlobalAndOwnOverrides_NeverAnotherTenants()
    {
        await SeedGlobalMapAsync();
        await SeedOverrideAsync(Tenant, "Tenant1-Overrides", "1001", "T11.1");
        await SeedOverrideAsync(OtherTenant, OtherTenantOverrideMap, "1001", "B99.9");

        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync("/admin/maps");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = (await ReadJsonAsync(response)).EnumerateArray()
            .Select(v => v.GetProperty("mapName").GetString()).ToList();
        Assert.Contains(GlobalMap, names);
        Assert.Contains("Tenant1-Overrides", names);
        Assert.DoesNotContain(OtherTenantOverrideMap, names);
    }

    // ── Writes: global maps need platform:admin ────────────────────────

    [Fact]
    public async Task LoadGlobalMap_TenantAdmin_IsForbidden_AndChangesNothing()
    {
        await SeedGlobalMapAsync();
        var versionsBefore = (await MapVersionsAsync()).Count;

        using var response = await Client(Tenant, ChoRolePermissions.TenantAdmin).PostAsync(
            LoadPath(GlobalMap, "202699", isOverride: false, tenantId: null), Csv("1001", "Z99.9", "Injected"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(versionsBefore, (await MapVersionsAsync()).Count);
        Assert.All(await MapVersionsAsync(), v => Assert.True(v["IsActive"].AsBoolean));
        Assert.DoesNotContain(await EntriesAsync(), e => e["TargetCode"].AsString == "Z99.9");
    }

    [Fact]
    public async Task LoadGlobalMap_ServiceToken_IsForbidden()
    {
        using var response = await ServiceClient(Tenant).PostAsync(
            LoadPath(GlobalMap, "202699", isOverride: false, tenantId: null), Csv("1001", "Z99.9", "Injected"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await MapVersionsAsync());
    }

    [Fact]
    public async Task LoadGlobalMap_WithATenantId_IsRejected()
    {
        // Non-override entries are read by every tenant whatever TenantId they carry.
        using var response = await PlatformAdmin().PostAsync(
            LoadPath("Tenant-Scoped-Base", "1", isOverride: false, tenantId: Tenant), Csv("1001", "Z99.9", "Injected"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task LoadGlobalMap_PlatformAdmin_RecordsTheActorFromTheToken()
    {
        await SeedGlobalMapAsync();

        var version = Assert.Single(await MapVersionsAsync());
        Assert.Equal(Subject, version.GetValue("ImportedBy", BsonNull.Value).ToString());
        Assert.True(version.GetValue("TenantId", BsonNull.Value).IsBsonNull);
    }

    // ── Writes: overrides belong to the token tenant ───────────────────

    [Fact]
    public async Task LoadOverride_ForAnotherTenant_IsForbidden_AndWritesNothing()
    {
        using var response = await Client(Tenant, ChoRolePermissions.TenantAdmin).PostAsync(
            LoadPath("Hostile-Overrides", "1", isOverride: true, tenantId: OtherTenant), Csv("1001", "Z99.9", "Injected"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await EntriesAsync());
        Assert.Empty(await MapVersionsAsync());
    }

    [Fact]
    public async Task LoadOverride_WithoutTenantParameter_IsScopedToTheTokenTenant()
    {
        using var response = await Client(Tenant, ChoRolePermissions.TenantAdmin).PostAsync(
            LoadPath("Tenant1-Overrides", "1", isOverride: true, tenantId: null), Csv("1001", "T11.1", "Mine"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(Tenant, entry["TenantId"].AsString);
        var version = Assert.Single(await MapVersionsAsync());
        Assert.Equal(Tenant, version.GetValue("TenantId", BsonNull.Value).ToString());
        Assert.Equal(Subject, version.GetValue("ImportedBy", BsonNull.Value).ToString());
    }

    [Fact]
    public async Task LoadOverride_WithoutSettingsManage_IsForbidden()
    {
        using var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).PostAsync(
            LoadPath("Tenant1-Overrides", "1", isOverride: true, tenantId: Tenant), Csv("1001", "T11.1", "Mine"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Rf2OverrideLoad_NamedLikeTheGlobalMap_DoesNotDeactivateIt()
    {
        await SeedGlobalMapAsync();

        using var response = await Client(OtherTenant, ChoRolePermissions.TenantAdmin).PostAsync(
            LoadPath(GlobalMap, "2026Q1", isOverride: true, tenantId: OtherTenant, format: "RF2"), Rf2("1001", "B99.9"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Another tenant still translates with the global map.
        using var translate = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync(TranslatePath("1001"));
        Assert.Equal(HttpStatusCode.OK, translate.StatusCode);
        Assert.Equal(["A00.0"], MatchCodes(await ReadJsonAsync(translate)));
    }
}
