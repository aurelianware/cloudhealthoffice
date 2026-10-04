using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests.Security;

/// <summary>
/// The real pipeline (CHO tokens through ChoDevelopmentTokenHandler, the
/// "PricingApiKey" scheme, shared tenant middleware) with stubbed repositories.
/// </summary>
public class PricingApiAuthTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string ValidKey = "cho_0123456789abcdef0123456789abcdef";

    private readonly PricingApiFactory _factory = new();

    public PricingApiAuthTests()
    {
        var schedules = new List<FeeScheduleInfo>
        {
            Schedule("MEDICARE_RBRVS_2025", FeeScheduleType.MedicareRbrvs),
            Schedule("ACME_COMMERCIAL_2025", FeeScheduleType.Commercial),
        };
        _factory.FeeScheduleRepository.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(schedules);
        foreach (var s in schedules)
        {
            _factory.FeeScheduleRepository.Setup(r => r.GetScheduleInfoAsync(s.Id)).ReturnsAsync(s);
            _factory.FeeScheduleRepository
                .Setup(r => r.LookupCodeAsync(s.Id, "99213", It.IsAny<string?>()))
                .ReturnsAsync(new FeeScheduleEntry { FeeScheduleId = s.Id, ProcedureCode = "99213", NonFacilityRate = 92.50m });
            _factory.FeeScheduleRepository
                .Setup(r => r.LookupCodesAsync(s.Id, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
                .ReturnsAsync([new FeeScheduleEntry { FeeScheduleId = s.Id, ProcedureCode = "99213", NonFacilityRate = 92.50m }]);
        }

        _factory.ApiKeyRepository.Setup(r => r.GetByKeyAsync(ValidKey)).ReturnsAsync(Key(ValidKey));
        _factory.ApiKeyRepository.Setup(r => r.CreateAsync(It.IsAny<ApiKeyRecord>()))
            .ReturnsAsync((ApiKeyRecord r) => r);
    }

    public void Dispose() => _factory.Dispose();

    // ── Clients ───────────────────────────────────────────────────────

    private HttpClient Anonymous() => _factory.CreateClient();

    private HttpClient Cho(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient WithApiKey(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        return client;
    }

    private HttpClient Service()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", Tenant));
        return client;
    }

    private static object RepriceBody(string scheduleId = "MEDICARE_RBRVS_2025") => new
    {
        feeScheduleId = scheduleId,
        claimType = "professional",
        placeOfService = "11",
        lines = new[] { new { lineNumber = 1, procedureCode = "99213", units = 1 } }
    };

    // ── Repricing: API-key customers ──────────────────────────────────

    [Fact]
    public async Task Reprice_Anonymous_Is401()
    {
        var response = await Anonymous().PostAsJsonAsync("/api/v1/reprice", RepriceBody());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reprice_WithValidApiKey_PricesAndMetersTheKey()
    {
        var response = await WithApiKey(ValidKey).PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-RateLimit-Limit").Should().Equal("1000");
        _factory.ApiKeyRepository.Verify(r => r.IncrementUsageAsync(ValidKey, 1), Times.Once);
        _factory.UsageRepository.Verify(r => r.RecordUsageAsync(It.Is<UsageRecord>(u => u.ApiKey == ValidKey)), Times.Once);
    }

    [Theory]
    [InlineData("abc")]      // shorter than the 8 characters the old middleware logged: 500 before
    [InlineData("cho_nope")]
    public async Task Reprice_WithUnknownApiKey_Is401(string key)
    {
        var response = await WithApiKey(key).PostAsJsonAsync("/api/v1/reprice", RepriceBody());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reprice_WithDeactivatedApiKey_Is401()
    {
        const string key = "cho_deactivated000000000000000000";
        _factory.ApiKeyRepository.Setup(r => r.GetByKeyAsync(key)).ReturnsAsync(Key(key) with { IsActive = false });

        var response = await WithApiKey(key).PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reprice_ApiKeyOverMonthlyQuota_Is429AndNothingIsPriced()
    {
        const string key = "cho_exhausted00000000000000000000";
        _factory.ApiKeyRepository.Setup(r => r.GetByKeyAsync(key)).ReturnsAsync(Key(key) with { CurrentMonthUsage = 1_000 });

        var response = await WithApiKey(key).PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _factory.FeeScheduleRepository.Verify(r => r.GetScheduleInfoAsync(It.IsAny<string>()), Times.Never);
        _factory.ApiKeyRepository.Verify(r => r.IncrementUsageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Reprice_ApiKeyAndBearerTogether_Is401()
    {
        var client = WithApiKey(ValidKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ClaimsExaminer));

        var response = await client.PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reprice_ApiKeyNamingACHOTenantInHeader_Is403()
    {
        // The key's tenant is its own credential; a header can never move it into a CHO tenant.
        var client = WithApiKey(ValidKey);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.ApiKeyRepository.Verify(r => r.IncrementUsageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    // ── Repricing: CHO callers ────────────────────────────────────────

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]    // claims:work
    [InlineData(ChoRolePermissions.MemberServices)]    // benefits:read
    [InlineData(ChoRolePermissions.ProviderRelations)] // contracts:read
    public async Task Reprice_ChoCallerWithPricingPermission_PricesWithoutMetering(string role)
    {
        var response = await Cho(role).PostAsJsonAsync("/api/v1/reprice", RepriceBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ApiKeyRepository.Verify(r => r.IncrementUsageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RepriceBatch_ServiceToken_Succeeds()
    {
        var response = await Service().PostAsJsonAsync("/api/v1/reprice/batch", new[] { RepriceBody() });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.ComplianceViewer)]
    public async Task Reprice_ChoCallerWithoutPricingPermission_Is403(string role)
    {
        var response = await Cho(role).PostAsJsonAsync("/api/v1/reprice", RepriceBody());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Anonymous reads: CMS-published data only ──────────────────────

    [Fact]
    public async Task Lookup_Anonymous_MedicareSchedule_Is200()
    {
        var response = await Anonymous().GetAsync("/api/v1/lookup/99213?feeScheduleId=MEDICARE_RBRVS_2025");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lookup_Anonymous_NonCmsSchedule_IsNotServed()
    {
        var response = await Anonymous().GetAsync("/api/v1/lookup/99213?feeScheduleId=ACME_COMMERCIAL_2025");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookup_ChoCallerWithPricingPermission_NonCmsSchedule_Is200()
    {
        var response = await Cho(ChoRolePermissions.ProviderRelations)
            .GetAsync("/api/v1/lookup/99213?feeScheduleId=ACME_COMMERCIAL_2025");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lookup_ChoCallerWithoutPricingPermission_NonCmsSchedule_IsNotServed()
    {
        var response = await Cho(ChoRolePermissions.FinanceApprover)
            .GetAsync("/api/v1/lookup/99213?feeScheduleId=ACME_COMMERCIAL_2025");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FeeSchedules_Anonymous_ListsCmsSchedulesOnly()
    {
        var response = await Anonymous().GetAsync("/api/v1/fee-schedules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ScheduleIds(response)).Should().Equal("MEDICARE_RBRVS_2025");
        (await Anonymous().GetAsync("/api/v1/fee-schedules/ACME_COMMERCIAL_2025"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FeeSchedules_ApiKeyCustomer_ListsAll()
    {
        var response = await WithApiKey(ValidKey).GetAsync("/api/v1/fee-schedules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ScheduleIds(response)).Should().BeEquivalentTo("MEDICARE_RBRVS_2025", "ACME_COMMERCIAL_2025");
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        (await Anonymous().GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Signup: no longer anonymous ───────────────────────────────────

    [Fact]
    public async Task Signup_Anonymous_Is401AndMintsNoKey()
    {
        var response = await Anonymous().PostAsJsonAsync("/api/v1/signup",
            new { organizationName = "Anyone", email = "a@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.ApiKeyRepository.Verify(r => r.CreateAsync(It.IsAny<ApiKeyRecord>()), Times.Never);
    }

    // ── Admin: global data, platform:admin only ───────────────────────

    public static TheoryData<string, string> AdminEndpoints => new()
    {
        { "POST", "/api/v1/admin/api-keys" },
        { "GET", "/api/v1/admin/api-keys" },
        { "DELETE", "/api/v1/admin/api-keys/" + ValidKey },
        { "POST", "/api/v1/admin/api-keys/reset-usage" },
        { "POST", "/api/v1/admin/fee-schedules/upload/rbrvs" },
        { "POST", "/api/v1/admin/fee-schedules/upload/opps" },
        { "POST", "/api/v1/admin/fee-schedules/upload/drg" },
        { "POST", "/api/v1/admin/fee-schedules/seed-demo" },
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_TenantAdminToken_Is403(string method, string path)
    {
        // TenantAdmin holds *:*, which never satisfies a platform permission.
        var response = await Cho(ChoRolePermissions.TenantAdmin).SendAsync(AdminRequest(method, path));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNoAdminWrite();
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_ServiceToken_Is403(string method, string path)
    {
        var response = await Service().SendAsync(AdminRequest(method, path));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNoAdminWrite();
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_ApiKeyCustomer_Is403(string method, string path)
    {
        var response = await WithApiKey(ValidKey).SendAsync(AdminRequest(method, path));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNoAdminWrite();
    }

    [Fact]
    public async Task Admin_SharedAdminSecretWithoutToken_Is401()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["PricingApi:AdminSecret"] = "s3cret" })));
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/api-keys")
        {
            Content = JsonContent.Create(new { tenantName = "Acme", tier = "free" })
        };
        request.Headers.Add("X-Admin-Secret", "s3cret");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.ApiKeyRepository.Verify(r => r.CreateAsync(It.IsAny<ApiKeyRecord>()), Times.Never);
    }

    [Fact]
    public async Task Admin_CreateApiKey_PlatformAdmin_RecordsActorFromToken()
    {
        var response = await Cho(ChoRolePermissions.PlatformAdmin).PostAsJsonAsync("/api/v1/admin/api-keys",
            new { tenantName = "Acme Health", contactEmail = "ops@acme.example", tier = "starter" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.ApiKeyRepository.Verify(r => r.CreateAsync(It.Is<ApiKeyRecord>(k =>
            k.CreatedBy == "user-1" && k.TenantName == "Acme Health" && k.MonthlyLimit == 10_000)), Times.Once);
    }

    [Fact]
    public async Task Admin_DeactivateApiKey_PlatformAdmin_RecordsActorFromToken()
    {
        var response = await Cho(ChoRolePermissions.PlatformAdmin).DeleteAsync("/api/v1/admin/api-keys/" + ValidKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ApiKeyRepository.Verify(r => r.DeactivateAsync(ValidKey, "user-1", It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Fact]
    public async Task Admin_SeedDemo_PlatformAdmin_Succeeds()
    {
        var response = await Cho(ChoRolePermissions.PlatformAdmin).PostAsync("/api/v1/admin/fee-schedules/seed-demo", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.FeeScheduleLoaderService.Verify(l => l.SeedDemoDataAsync(), Times.Once);
    }

    [Fact]
    public async Task Signup_PlatformAdmin_RecordsActorFromToken()
    {
        var response = await Cho(ChoRolePermissions.PlatformAdmin).PostAsJsonAsync("/api/v1/signup",
            new { organizationName = "Acme", email = "a@acme.example" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.ApiKeyRepository.Verify(r => r.CreateAsync(It.Is<ApiKeyRecord>(k => k.CreatedBy == "user-1")), Times.Once);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void VerifyNoAdminWrite()
    {
        _factory.ApiKeyRepository.Verify(r => r.CreateAsync(It.IsAny<ApiKeyRecord>()), Times.Never);
        _factory.ApiKeyRepository.Verify(r => r.ResetMonthlyUsageAsync(), Times.Never);
        _factory.ApiKeyRepository.Verify(r => r.DeactivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _factory.FeeScheduleLoaderService.Verify(l => l.SeedDemoDataAsync(), Times.Never);
        _factory.FeeScheduleLoaderService.Verify(l => l.SeedMedicareRbrvs(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _factory.FeeScheduleLoaderService.Verify(l => l.SeedMedicareOpps(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _factory.FeeScheduleLoaderService.Verify(l => l.SeedMedicareDrg(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    private static HttpRequestMessage AdminRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.Contains("/upload/"))
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent("HCPCS,WORK RVU\n99213,1.3\n"u8.ToArray()), "file", "rates.csv");
            request.Content = form;
        }
        else if (method == "POST" && path.EndsWith("/api-keys"))
        {
            request.Content = JsonContent.Create(new { tenantName = "Acme", tier = "free" });
        }
        return request;
    }

    private static async Task<List<string>> ScheduleIds(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .ToList();
    }

    private static FeeScheduleInfo Schedule(string id, FeeScheduleType type) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Version = "2025",
        EffectiveDate = new DateOnly(2025, 1, 1),
        CodeCount = 1,
        LastUpdated = DateTimeOffset.UtcNow
    };

    private static ApiKeyRecord Key(string key) => new()
    {
        ApiKey = key,
        TenantName = "Acme Health",
        Tier = PricingTier.Free,
        MonthlyLimit = 1_000,
        CurrentMonthUsage = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        IsActive = true
    };
}
