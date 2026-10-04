using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using CloudHealthOffice.ReferenceData.Sources;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ReferenceDataService.Controllers;
using ReferenceDataService.Models;
using ReferenceDataService.Repositories;
using Xunit;
using CanonicalRepository = CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository;
using LegacyRepository = ReferenceDataService.Repositories.IReferenceDataRepository;

namespace CloudHealthOffice.ReferenceDataService.Tests.Security;

/// <summary>
/// The real reference-data-service pipeline. Every caller needs a CHO token and
/// the tenant comes from it. Compliance config is per-tenant and its
/// <c>{tenantId}</c> route segment can only echo the token tenant. Canonical
/// codes without a tenant are global: writing them needs platform:admin.
/// </summary>
public class ReferenceDataPipelineAuthTests : IClassFixture<ReferenceDataPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ComplianceConfigController>
    {
        public InMemoryComplianceConfigRepository Compliance { get; private set; } = new();
        public InMemoryReferenceDataRepository Canonical { get; private set; } = new();
        public Mock<LegacyRepository> Legacy { get; } = new();

        public void Reset()
        {
            Compliance = new InMemoryComplianceConfigRepository();
            Canonical = new InMemoryReferenceDataRepository();
            Legacy.Reset();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads these while building, so they go in as host settings.
            builder.UseSetting("ConnectionStrings:PostgreSQL", "Host=unused.invalid;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ReferenceData:ApplySchemaMigrationsOnStartup", "false");
            builder.UseSetting("MongoDb:ConnectionString", "");
            builder.UseSetting("Database:Provider", "");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IComplianceConfigRepository>();
                services.AddScoped<IComplianceConfigRepository>(_ => Compliance);
                services.RemoveAll<CanonicalRepository>();
                services.AddScoped<CanonicalRepository>(_ => Canonical);
                services.RemoveAll<LegacyRepository>();
                services.AddScoped(_ => Legacy.Object);
            });
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Subject = "refdata-user-7";
    private readonly Factory _factory;

    public ReferenceDataPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Reset();
        // The response cache is shared by the host; each test seeds fresh data.
        var cache = _factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        cache.Remove($"compliance:{Tenant}");
        cache.Remove($"compliance:{OtherTenant}");
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(Subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient ServiceClient(string tenant, string clientId = "encounter-submission-service")
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }

    private async Task SeedComplianceAsync(string tenant, int encounterDays)
        => await _factory.Compliance.UpsertAsync(new TenantComplianceConfig
        {
            TenantId = tenant,
            StateCode = "FL",
            FmmisSubmitterId = "SUB-" + tenant,
            StateConfig = new StateComplianceConfig { EncounterSubmissionDays = encounterDays }
        });

    private static object ConfigBody(string? tenantId = null, string? actor = null) => new
    {
        tenantId,
        stateCode = "FL",
        fmmisSubmitterId = "NEW-SUBMITTER",
        stateConfig = new { encounterSubmissionDays = 45 },
        createdBy = actor,
        updatedBy = actor
    };

    // ── Authentication ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/compliance-config/tenant-1")]
    [InlineData("/api/compliance-config/tenant-1/state")]
    [InlineData("/api/ReferenceData/icd10/E119/validate")]
    [InlineData("/api/reference-data/codes/CPT/99213")]
    [InlineData("/api/reference-data/codes?codeSystem=CPT")]
    public async Task NoToken_IsRejected(string path)
    {
        await SeedComplianceAsync(Tenant, 30);

        var response = await _factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_CannotReadComplianceConfig()
    {
        await SeedComplianceAsync(Tenant, 30);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/compliance-config/{Tenant}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DevSeedWithoutToken_IsRejected()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync($"/api/compliance-config/{Tenant}/dev-seed", ConfigBody(Tenant), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.Compliance.GetAsync(Tenant)).Should().BeNull();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        await SeedComplianceAsync(OtherTenant, 30);
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/compliance-config/{OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Compliance config: per-tenant, route tenant must match the token ─

    [Fact]
    public async Task StateConfig_IsServedForTokenTenant()
    {
        await SeedComplianceAsync(Tenant, 30);

        var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .GetFromJsonAsync<StateComplianceConfig>($"/api/compliance-config/{Tenant}/state", Json);

        response!.EncounterSubmissionDays.Should().Be(30);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/state")]
    public async Task PathTenantOtherThanTokenTenant_CannotReadComplianceConfig(string suffix)
    {
        await SeedComplianceAsync(Tenant, 30);
        await SeedComplianceAsync(OtherTenant, 90);

        var response = await Client(Tenant).GetAsync($"/api/compliance-config/{OtherTenant}{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("SUB-" + OtherTenant).And.NotContain("90");
    }

    [Fact]
    public async Task PathTenantOtherThanTokenTenant_CannotWriteComplianceConfig()
    {
        await SeedComplianceAsync(OtherTenant, 90);

        var response = await Client(Tenant).PutAsJsonAsync($"/api/compliance-config/{OtherTenant}", ConfigBody(OtherTenant), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stored = await _factory.Compliance.GetAsync(OtherTenant);
        stored!.FmmisSubmitterId.Should().Be("SUB-" + OtherTenant);
        stored.StateConfig.EncounterSubmissionDays.Should().Be(90);
        (await _factory.Compliance.GetAsync(Tenant)).Should().BeNull();
    }

    [Fact]
    public async Task PathTenantOtherThanTokenTenant_CannotDevSeed()
    {
        var response = await Client(Tenant).PostAsJsonAsync($"/api/compliance-config/{OtherTenant}/dev-seed", ConfigBody(OtherTenant), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.Compliance.GetAsync(OtherTenant)).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_StoresUnderTokenTenant_AndIgnoresBodyTenant()
    {
        // The body names another tenant; the path and token name tenant-1.
        var response = await Client(Tenant).PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(OtherTenant), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.Compliance.GetAsync(OtherTenant)).Should().BeNull();
        (await _factory.Compliance.GetAsync(Tenant))!.FmmisSubmitterId.Should().Be("NEW-SUBMITTER");
    }

    [Fact]
    public async Task Upsert_WithoutBodyTenant_IsAccepted()
    {
        var response = await Client(Tenant).PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(tenantId: null), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.Compliance.GetAsync(Tenant))!.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task Upsert_RecordsActorFromToken_NotFromBody()
    {
        var response = await Client(Tenant).PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(Tenant, actor: "forged-user"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.Compliance.GetAsync(Tenant);
        stored!.CreatedBy.Should().Be(Subject);
        stored.UpdatedBy.Should().Be(Subject);
    }

    [Fact]
    public async Task Upsert_KeepsCreator_AndRecordsLatestWriter()
    {
        await Client(Tenant).PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(Tenant), Json);
        var second = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("second-admin", ChoRolePermissions.TenantAdmin));
        second.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await second.PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(Tenant, actor: Subject), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.Compliance.GetAsync(Tenant);
        stored!.CreatedBy.Should().Be(Subject);
        stored.UpdatedBy.Should().Be("second-admin");
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]   // reference-data:read only
    [InlineData(ChoRolePermissions.ComplianceOfficer)] // *:read
    public async Task RoleWithoutSettingsManage_CannotWriteComplianceConfig(string role)
    {
        var client = Client(Tenant, role);

        var put = await client.PutAsJsonAsync($"/api/compliance-config/{Tenant}", ConfigBody(Tenant), Json);
        var seed = await client.PostAsJsonAsync($"/api/compliance-config/{Tenant}/dev-seed", ConfigBody(Tenant), Json);

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        seed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.Compliance.GetAsync(Tenant)).Should().BeNull();
    }

    [Fact]
    public async Task RoleWithoutReferenceDataRead_CannotReadComplianceConfig()
    {
        await SeedComplianceAsync(Tenant, 30);

        // Finance holds no reference-data permission.
        var response = await Client(Tenant, ChoRolePermissions.Finance).GetAsync($"/api/compliance-config/{Tenant}/state");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_CanReadItsTenantsStateConfig()
    {
        // encounter-submission-service's worker reads this with a service token for the record's tenant.
        await SeedComplianceAsync(Tenant, 30);

        var response = await ServiceClient(Tenant).GetFromJsonAsync<StateComplianceConfig>($"/api/compliance-config/{Tenant}/state", Json);

        response!.EncounterSubmissionDays.Should().Be(30);
    }

    [Fact]
    public async Task ServiceToken_CannotReadAnotherTenantsConfig()
    {
        await SeedComplianceAsync(OtherTenant, 90);

        var response = await ServiceClient(Tenant).GetAsync($"/api/compliance-config/{OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Legacy code sets: global, read-only ──────────────────────────────

    [Fact]
    public async Task LegacyCodeLookup_NeedsReferenceDataRead()
    {
        _factory.Legacy.Setup(r => r.GetIcd10CodeAsync("E119"))
            .ReturnsAsync(new Icd10Code { Code = "E119", ShortDescription = "Type 2 diabetes", StatusCode = "A", Billable = true });

        var examiner = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync("/api/ReferenceData/icd10/E119/validate");
        var finance = await Client(Tenant, ChoRolePermissions.Finance).GetAsync("/api/ReferenceData/icd10/E119/validate");

        examiner.StatusCode.Should().Be(HttpStatusCode.OK);
        finance.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Canonical codes: global vs tenant-scoped ─────────────────────────

    private static ReferenceCode Code(string code, string? tenantId, string checksum, ExposureClassification exposure = ExposureClassification.AuthenticatedReference) => new()
    {
        Id = $"cpt-{code}-{tenantId ?? "global"}",
        TenantId = tenantId,
        Coding = new ChoCoding { CodeSystem = "CPT", Code = code, Version = "2026", Display = "Display " + code },
        Description = "Description " + code,
        EffectiveFrom = new DateOnly(2026, 1, 1),
        SourceId = "source-" + checksum,
        SourceVersion = "2026",
        LicenseClassification = LicenseClassification.Licensed,
        ExposureClassification = exposure,
        ImportedAt = DateTimeOffset.UtcNow,
        Checksum = checksum
    };

    private async Task<int> CountAsync(string tenant)
        => (await _factory.Canonical.SearchAsync(new ReferenceDataQuery { CodeSystem = "CPT", TenantId = tenant })).Total;

    [Fact]
    public async Task TenantAdmin_CannotImportGlobalCodes()
    {
        var response = await Client(Tenant, ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", null, "g1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(OtherTenant)).Should().Be(0, "a global record would be visible to every tenant");
    }

    [Fact]
    public async Task TenantAdmin_CannotSlipGlobalCodeIntoTenantBatch()
    {
        var response = await Client(Tenant, ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", Tenant, "m1"), Code("99214", null, "m1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(Tenant)).Should().Be(0);
    }

    [Fact]
    public async Task ServiceToken_CannotImportGlobalCodes()
    {
        var response = await ServiceClient(Tenant)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", null, "s1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(OtherTenant)).Should().Be(0);
    }

    [Fact]
    public async Task PlatformAdmin_CanImportGlobalCodes_VisibleToEveryTenant()
    {
        var response = await Client(Tenant, ChoRolePermissions.PlatformAdmin)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", null, "p1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(OtherTenant)).Should().Be(1);
    }

    [Fact]
    public async Task TenantAdmin_CanImportOwnTenantCodes_NotVisibleToOtherTenants()
    {
        var response = await Client(Tenant, ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", Tenant, "t1", ExposureClassification.TenantRestricted) }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(Tenant)).Should().Be(1);
        (await CountAsync(OtherTenant)).Should().Be(0);
    }

    [Fact]
    public async Task Import_ForAnotherTenant_IsForbidden()
    {
        var response = await Client(Tenant, ChoRolePermissions.PlatformAdmin)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", OtherTenant, "o1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(OtherTenant)).Should().Be(0);
    }

    [Fact]
    public async Task RoleWithoutSettingsManage_CannotImportTenantCodes()
    {
        var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .PostAsJsonAsync("/api/reference-data/codes/import", new[] { Code("99213", Tenant, "e1") }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(Tenant)).Should().Be(0);
    }

    [Fact]
    public async Task CanonicalSearch_IsScopedToTokenTenant()
    {
        await _factory.Canonical.ImportAsync([Code("99213", Tenant, "r1", ExposureClassification.TenantRestricted)]);
        await _factory.Canonical.ImportAsync([Code("99214", OtherTenant, "r2", ExposureClassification.TenantRestricted)]);

        var page = await Client(Tenant, ChoRolePermissions.ClaimsExaminer)
            .GetFromJsonAsync<Page<ReferenceCode>>("/api/reference-data/codes?codeSystem=CPT", Json);

        page!.Items.Should().ContainSingle().Which.Coding.Code.Should().Be("99213");
        page.Items[0].Coding.Display.Should().Be("Display 99213");
    }

    [Fact]
    public async Task InternalOnlyText_IsRedactedForTenantUsers_AndReadableByServices()
    {
        await _factory.Canonical.ImportAsync([Code("99213", null, "i1", ExposureClassification.InternalOnly)]);

        var user = await Client(Tenant, ChoRolePermissions.TenantAdmin)
            .GetFromJsonAsync<ReferenceCode>("/api/reference-data/codes/CPT/99213?effectiveDate=2026-08-14", Json);
        var service = await ServiceClient(Tenant, "claims-service")
            .GetFromJsonAsync<ReferenceCode>("/api/reference-data/codes/CPT/99213?effectiveDate=2026-08-14", Json);

        user!.Coding.Display.Should().BeNull();
        user.Description.Should().BeNull();
        service!.Coding.Display.Should().Be("Display 99213");
    }
}
