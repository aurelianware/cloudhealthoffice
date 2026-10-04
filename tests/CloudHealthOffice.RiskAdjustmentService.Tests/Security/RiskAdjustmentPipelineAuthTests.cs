using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RiskAdjustmentService.Controllers;
using RiskAdjustmentService.Models;
using RiskAdjustmentService.Repositories;

namespace CloudHealthOffice.RiskAdjustmentService.Tests.Security;

/// <summary>
/// The real risk-adjustment-service pipeline. Risk scores carry diagnoses
/// (PHI), so every caller needs a CHO token; the tenant and the acting user
/// come from it, never from a header, query string or body.
///
/// Before this change the service had no authentication: the local
/// TenantMiddleware took the tenant from an unauthenticated X-Tenant-ID /
/// X-Dev-Tenant-ID header and fell back to "default-tenant", and request
/// bodies supplied CreatedBy / LastUpdatedBy.
/// </summary>
public class RiskAdjustmentPipelineAuthTests : IClassFixture<RiskAdjustmentPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<RiskAdjustmentController>
    {
        public InMemoryRiskScoreStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRiskScoreRepository>();
                services.AddScoped<IRiskScoreRepository>(sp =>
                    new InMemoryRiskScoreRepository(Store, sp.GetRequiredService<IHttpContextAccessor>()));
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "risk-user-7";
    private const int Year = 2026;
    private readonly Factory _factory;

    public RiskAdjustmentPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Store.Clear();
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private MemberRiskScore Seed(string tenant, string memberId, decimal score = 1.234m)
    {
        var record = new MemberRiskScore
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenant,
            MemberId = memberId,
            MeasurementYear = Year,
            LineOfBusiness = LineOfBusiness.Medicare,
            Status = ScoreStatus.Calculated,
            RiskScore = score,
            DemographicFactor = 0.5m,
            CreatedBy = "seed",
            Diagnoses = [new RiskDiagnosis { DiagnosisCode = "E1122", MappedHccCategory = "37" }]
        };
        _factory.Store.Put(record);
        return record;
    }

    /// <summary>A score body; with no tenant the field is left out, as a caller that only has a token sends it.</summary>
    private static object ScoreBody(string memberId, string? tenantId, string? actor = null) => tenantId == null
        ? new
        {
            memberId,
            measurementYear = Year,
            lineOfBusiness = "Medicare",
            riskModel = "CMS-HCC",
            modelVersion = "V28",
            riskScore = 1.5m,
            demographicFactor = 0.4m,
            createdBy = actor,
            lastUpdatedBy = actor
        }
        : (object)new
        {
            tenantId,
            memberId,
            measurementYear = Year,
            lineOfBusiness = "Medicare",
            riskModel = "CMS-HCC",
            modelVersion = "V28",
            riskScore = 1.5m,
            demographicFactor = 0.4m,
            createdBy = actor,
            lastUpdatedBy = actor
        };

    private static object CalculateBody(string memberId) => new
    {
        memberId,
        measurementYear = Year,
        lineOfBusiness = "Medicare",
        riskModel = "CMS-HCC",
        modelVersion = "V28",
        ageAsOfPaymentYear = 70,
        gender = "F",
        diagnosisCodes = new[] { "E1122" }
    };

    // ── Authentication ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/risk-adjustment/members/m-1/scores/2026")]
    [InlineData("/api/risk-adjustment/members/m-1/scores/2026/summary")]
    [InlineData("/api/risk-adjustment/members/m-1/scores")]
    [InlineData("/api/risk-adjustment/measurement-years/2026/scores")]
    [InlineData("/api/risk-adjustment/measurement-years/2026/summary")]
    [InlineData("/api/risk-adjustment/scores/search?measurementYear=2026")]
    public async Task NoToken_ReadIsRejected(string path)
    {
        Seed(Tenant, "m-1");

        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_WritesAreRejected()
    {
        var existing = Seed(Tenant, "m-1");
        var client = _factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/risk-adjustment/members/m-2/scores/{Year}", ScoreBody("m-2", Tenant));
        var calc = await client.PostAsJsonAsync("/api/risk-adjustment/scores/calculate", CalculateBody("m-3"));
        var batch = await client.PostAsJsonAsync("/api/risk-adjustment/scores/batch-status",
            new { memberIds = new[] { "m-1" }, measurementYear = Year, status = "Submitted" });
        var delete = await client.DeleteAsync($"/api/risk-adjustment/scores/{existing.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, calc.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, batch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Single(_factory.Store.All());
        Assert.Equal(ScoreStatus.Calculated, _factory.Store.All().Single().Status);
    }

    [Theory]
    [InlineData("X-Tenant-ID")]
    [InlineData("X-Dev-Tenant-ID")]
    public async Task TenantHeaderWithoutToken_CannotReadScores(string header)
    {
        Seed(Tenant, "m-1");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, Tenant);

        var response = await client.GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoTenantAnywhere_DoesNotFallBackToDefaultTenant()
    {
        Seed("default-tenant", "m-1");

        var response = await _factory.CreateClient().GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsRejected()
    {
        Seed(OtherTenant, "m-1");
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(User, Tenant, [ChoRolePermissions.Finance]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenTenant_CannotReadAnotherTenantsScore()
    {
        var other = Seed(OtherTenant, "m-1");
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(User, Tenant, [ChoRolePermissions.Finance]));

        var byMember = await client.GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");
        var byId = await client.GetAsync($"/api/risk-adjustment/scores/{other.Id}");

        Assert.Equal(HttpStatusCode.NotFound, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    // ── Permissions: reads need risk-adjustment:read ─────────────────────

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task RolesWithRiskAdjustmentRead_CanReadScores(string role)
    {
        Seed(Tenant, "m-1");

        var response = await Client(Tenant, role).GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var score = await response.Content.ReadFromJsonAsync<MemberRiskScore>(Json);
        Assert.Equal("m-1", score!.MemberId);
    }

    [Theory]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.ComplianceViewer)]
    public async Task RolesWithoutRiskAdjustmentRead_CannotReadDiagnoses(string role)
    {
        Seed(Tenant, "m-1");
        var client = Client(Tenant, role);

        var score = await client.GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");
        var search = await client.GetAsync($"/api/risk-adjustment/scores/search?measurementYear={Year}");
        var year = await client.GetAsync($"/api/risk-adjustment/measurement-years/{Year}/scores");

        Assert.Equal(HttpStatusCode.Forbidden, score.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, year.StatusCode);
    }

    [Fact]
    public async Task CapitationServiceToken_CanReadTheScoreItUses()
    {
        Seed(Tenant, "m-1", 1.75m);
        var client = BearerClient(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("capitation-service", Tenant));
        // capitation-service sends X-Tenant-ID with every call; it matches the token.
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/risk-adjustment/members/m-1/scores/{Year}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var score = await response.Content.ReadFromJsonAsync<MemberRiskScore>(Json);
        Assert.Equal(1.75m, score!.RiskScore);
    }

    // ── Permissions: writes need risk-adjustment:write or finance:write ──

    [Theory]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task RolesWithoutWritePermission_CannotChangeScores(string role)
    {
        var existing = Seed(Tenant, "m-1");
        var client = Client(Tenant, role);

        var put = await client.PutAsJsonAsync($"/api/risk-adjustment/members/m-2/scores/{Year}", ScoreBody("m-2", null));
        var calc = await client.PostAsJsonAsync("/api/risk-adjustment/scores/calculate", CalculateBody("m-3"));
        var batch = await client.PostAsJsonAsync("/api/risk-adjustment/scores/batch-status",
            new { memberIds = new[] { "m-1" }, measurementYear = Year, status = "Submitted" });
        var delete = await client.DeleteAsync($"/api/risk-adjustment/scores/{existing.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, calc.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        var only = Assert.Single(_factory.Store.All());
        Assert.Equal(ScoreStatus.Calculated, only.Status);
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task RolesWithWritePermission_CanCalculateAndSubmit(string role)
    {
        var client = Client(Tenant, role);

        var calc = await client.PostAsJsonAsync("/api/risk-adjustment/scores/calculate", CalculateBody("m-3"));
        var batch = await client.PostAsJsonAsync("/api/risk-adjustment/scores/batch-status",
            new { memberIds = new[] { "m-3" }, measurementYear = Year, status = "Submitted" });

        Assert.Equal(HttpStatusCode.OK, calc.StatusCode);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var stored = Assert.Single(_factory.Store.All());
        Assert.Equal(Tenant, stored.TenantId);
        Assert.True(stored.IsSubmitted);
    }

    // ── Tenant and actor from the token ─────────────────────────────────

    [Fact]
    public async Task Upsert_DoesNotNeedTenantInBody()
    {
        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .PutAsJsonAsync($"/api/risk-adjustment/members/m-2/scores/{Year}", ScoreBody("m-2", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Tenant, Assert.Single(_factory.Store.All()).TenantId);
    }

    [Fact]
    public async Task Upsert_BodyTenantIsIgnored()
    {
        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .PutAsJsonAsync($"/api/risk-adjustment/members/m-2/scores/{Year}", ScoreBody("m-2", OtherTenant));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Tenant, Assert.Single(_factory.Store.All()).TenantId);
    }

    [Fact]
    public async Task UpsertCreate_ActorIsTokenSubject_NotBody()
    {
        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .PutAsJsonAsync($"/api/risk-adjustment/members/m-2/scores/{Year}", ScoreBody("m-2", Tenant, actor: "spoofed-user"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(_factory.Store.All());
        Assert.Equal(User, stored.CreatedBy);
        Assert.Equal(User, stored.LastUpdatedBy);
    }

    [Fact]
    public async Task UpsertUpdate_KeepsCreator_LastUpdatedByIsTokenSubject()
    {
        Seed(Tenant, "m-1");

        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .PutAsJsonAsync($"/api/risk-adjustment/members/m-1/scores/{Year}", ScoreBody("m-1", Tenant, actor: "spoofed-user"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(_factory.Store.All());
        Assert.Equal("seed", stored.CreatedBy);
        Assert.Equal(User, stored.LastUpdatedBy);
    }

    [Fact]
    public async Task Calculate_RecordsTokenSubject()
    {
        var created = await Client(Tenant, ChoRolePermissions.Finance)
            .PostAsJsonAsync("/api/risk-adjustment/scores/calculate", CalculateBody("m-3"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = Assert.Single(_factory.Store.All());
        Assert.Equal(User, first.CreatedBy);
        Assert.Equal(User, first.LastUpdatedBy);

        var other = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("risk-user-8", ChoRolePermissions.Finance));
        other.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        var recalculated = await other.PostAsJsonAsync("/api/risk-adjustment/scores/calculate", CalculateBody("m-3"));

        Assert.Equal(HttpStatusCode.OK, recalculated.StatusCode);
        var stored = Assert.Single(_factory.Store.All());
        Assert.Equal(User, stored.CreatedBy);
        Assert.Equal("risk-user-8", stored.LastUpdatedBy);
    }

    [Fact]
    public async Task BatchStatus_RecordsTokenSubject()
    {
        Seed(Tenant, "m-1");

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync("/api/risk-adjustment/scores/batch-status",
            new { memberIds = new[] { "m-1" }, measurementYear = Year, status = "Submitted" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(_factory.Store.All());
        Assert.Equal(ScoreStatus.Submitted, stored.Status);
        Assert.Equal(User, stored.LastUpdatedBy);
    }

    [Fact]
    public async Task BatchStatus_CannotTouchAnotherTenantsScores()
    {
        Seed(OtherTenant, "m-1");

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync("/api/risk-adjustment/scores/batch-status",
            new { memberIds = new[] { "m-1" }, measurementYear = Year, status = "Submitted" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ScoreStatus.Calculated, Assert.Single(_factory.Store.All()).Status);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

/// <summary>Risk scores shared by every request of one test host.</summary>
public sealed class InMemoryRiskScoreStore
{
    private readonly ConcurrentDictionary<string, MemberRiskScore> _rows = new();

    public void Clear() => _rows.Clear();

    public void Put(MemberRiskScore score) => _rows[score.Id] = score;

    public void Remove(string id) => _rows.TryRemove(id, out _);

    public IReadOnlyList<MemberRiskScore> All() => _rows.Values.ToList();
}

/// <summary>
/// Same tenant rules as the Mongo and Cosmos repositories: the tenant is
/// <c>HttpContext.Items["TenantId"]</c>, a missing tenant throws, reads filter
/// on it and writes stamp it.
/// </summary>
public sealed class InMemoryRiskScoreRepository : IRiskScoreRepository
{
    private readonly InMemoryRiskScoreStore _store;
    private readonly IHttpContextAccessor _accessor;

    public InMemoryRiskScoreRepository(InMemoryRiskScoreStore store, IHttpContextAccessor accessor)
    {
        _store = store;
        _accessor = accessor;
    }

    private string TenantId => _accessor.HttpContext?.Items["TenantId"] as string is { Length: > 0 } tenant
        ? tenant
        : throw new InvalidOperationException("TenantId not found in request context");

    private IEnumerable<MemberRiskScore> Rows()
    {
        var tenant = TenantId;
        return _store.All().Where(s => s.TenantId == tenant);
    }

    public Task<MemberRiskScore?> GetByIdAsync(string id) => Task.FromResult(Rows().FirstOrDefault(s => s.Id == id));

    public Task<MemberRiskScore?> GetByMemberAndYearAsync(string memberId, int measurementYear)
        => Task.FromResult(Rows().FirstOrDefault(s => s.MemberId == memberId && s.MeasurementYear == measurementYear));

    public Task<IEnumerable<MemberRiskScore>> GetByMemberAsync(string memberId)
        => Task.FromResult<IEnumerable<MemberRiskScore>>(Rows().Where(s => s.MemberId == memberId).ToList());

    public Task<IEnumerable<MemberRiskScore>> SearchAsync(int? measurementYear, string? memberId, LineOfBusiness? lineOfBusiness,
        ScoreStatus? status, decimal? minScore, decimal? maxScore, int page, int pageSize)
        => Task.FromResult<IEnumerable<MemberRiskScore>>(Rows()
            .Where(s => measurementYear == null || s.MeasurementYear == measurementYear)
            .Where(s => memberId == null || s.MemberId == memberId)
            .ToList());

    public Task<IEnumerable<MemberRiskScore>> GetByMeasurementYearAsync(int measurementYear, LineOfBusiness? lineOfBusiness, int page, int pageSize)
        => Task.FromResult<IEnumerable<MemberRiskScore>>(Rows().Where(s => s.MeasurementYear == measurementYear).ToList());

    public Task<MeasurementYearSummary> GetMeasurementYearSummaryAsync(int measurementYear, LineOfBusiness? lineOfBusiness)
        => Task.FromResult(new MeasurementYearSummary
        {
            MeasurementYear = measurementYear,
            TotalMembers = Rows().Count(s => s.MeasurementYear == measurementYear)
        });

    public Task<MemberRiskScore> CreateAsync(MemberRiskScore score)
    {
        score.TenantId = TenantId;
        _store.Put(score);
        return Task.FromResult(score);
    }

    public Task<MemberRiskScore> UpdateAsync(MemberRiskScore score)
    {
        score.TenantId = TenantId;
        _store.Put(score);
        return Task.FromResult(score);
    }

    public Task DeleteAsync(string id)
    {
        if (Rows().Any(s => s.Id == id))
            _store.Remove(id);
        return Task.CompletedTask;
    }
}
