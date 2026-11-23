using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CHO.TerminologyService.Controllers;
using CHO.TerminologyService.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests;

/// <summary>
/// Dated code sets through the real pipeline and Mongo: loading a CMS release
/// (platform:admin only) and $validate-code with a date of service
/// (terminology:read, service tokens included).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CodeSetPipelineTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string Subject = "terminology-user-7";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private WebApplicationFactory<TerminologyController> _factory = null!;

    public CodeSetPipelineTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("code_set_pipeline");
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

    private HttpClient Client(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(Subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient ServiceClient()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", Tenant));
        return client;
    }

    private static string LoadPath(string version, string? from = null)
        => $"/admin/code-sets/load?system={Uri.EscapeDataString(CodeSetSystems.Icd10Cm)}&format=ICD10-ORDER&version={version}"
           + (from is null ? string.Empty : $"&effectiveFrom={from}");

    private static StringContent OrderFile() => new(
        CodeSetValidationServiceTests.Fy2026Order(), Encoding.UTF8, "text/plain");

    private static string ValidatePath(string code, string date)
        => $"/fhir/CodeSystem/$validate-code?url={Uri.EscapeDataString(CodeSetSystems.Icd10Cm)}&code={code}&date={date}";

    private async Task LoadFy2026Async()
    {
        using var response = await Client(ChoRolePermissions.PlatformAdmin).PostAsync(LoadPath("FY2026"), OrderFile());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_Loads_ThenValidateCodeAnswersForTheDateOfService()
    {
        await LoadFy2026Async();
        var client = Client(ChoRolePermissions.ClaimsExaminer);

        var valid = await client.GetFromJsonAsync<CodeValidityResponse>(ValidatePath("E11.65", "2026-03-01"), Json);
        var header = await client.GetFromJsonAsync<CodeValidityResponse>(ValidatePath("E11", "2026-03-01"), Json);
        var uncovered = await client.GetFromJsonAsync<CodeValidityResponse>(ValidatePath("E11.65", "2026-10-01"), Json);

        Assert.Equal(CodeValidityStatus.Valid, valid!.Status);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", valid.Display);
        Assert.Equal(CodeValidityReason.NotBillable, header!.Reason);
        Assert.Equal(CodeValidityStatus.Unknown, uncovered!.Status);
    }

    [Fact]
    public async Task AprilUpdate_ThroughMongo_EndsTheEarlierRelease()
    {
        await LoadFy2026Async();
        using var april = await Client(ChoRolePermissions.PlatformAdmin).PostAsync(
            LoadPath("FY2026-APR", "2026-04-01"),
            new StringContent(CodeSetValidationServiceTests.OrderLine(1, "E1165", true, "x", "T2DM with hyperglycemia"), Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, april.StatusCode);

        var client = Client(ChoRolePermissions.ClaimsExaminer);
        var march = await client.GetFromJsonAsync<CodeValidityResponse>(ValidatePath("Z23", "2026-03-31"), Json);
        var aprilAnswer = await client.GetFromJsonAsync<CodeValidityResponse>(ValidatePath("Z23", "2026-04-01"), Json);

        Assert.True(march!.Result);
        Assert.Equal(CodeValidityReason.Terminated, aprilAnswer!.Reason);
    }

    [Fact]
    public async Task BatchValidate_ServiceToken_IsAllowed()
    {
        await LoadFy2026Async();

        using var response = await ServiceClient().PostAsJsonAsync("/fhir/CodeSystem/$batch-validate-code", new[]
        {
            new { system = CodeSetSystems.Icd10Cm, code = "Z23", date = "2026-03-01" },
            new { system = CodeSetSystems.Icd10Cm, code = "Q99.99", date = "2026-03-01" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answers = await response.Content.ReadFromJsonAsync<List<CodeValidityResponse>>(Json);
        Assert.Equal([true, false], answers!.Select(a => a.Result).ToArray());
    }

    [Fact]
    public async Task TenantAdmin_CannotLoadACodeSet()
    {
        using var response = await Client(ChoRolePermissions.TenantAdmin).PostAsync(LoadPath("FY2026"), OrderFile());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await _database.GetCollection<CodeSetRelease>("code_set_releases").CountDocumentsAsync(FilterDefinition<CodeSetRelease>.Empty));
    }

    [Theory]
    [InlineData("GET", "/fhir/CodeSystem/$validate-code?url=http%3A%2F%2Fhl7.org%2Ffhir%2Fsid%2Ficd-10-cm&code=Z23&date=2026-03-01")]
    [InlineData("GET", "/admin/code-sets")]
    [InlineData("POST", "/fhir/CodeSystem/$batch-validate-code")]
    public async Task NoToken_IsRejected(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(new[] { new { system = CodeSetSystems.Icd10Cm, code = "Z23" } });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidateCode_WithoutTerminologyRead_IsForbidden()
    {
        using var response = await Client(ChoRolePermissions.MemberServices).GetAsync(ValidatePath("Z23", "2026-03-01"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
