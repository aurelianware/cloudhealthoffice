using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.IdCardService.Tests.Security;

/// <summary>
/// The real pipeline with the real upstream clients; only the network is
/// replaced. Proves which token every outbound CHO call carries and that a
/// refusal is never read as "nothing configured" or "not found".
/// </summary>
public class IdCardUpstreamAuthTests : IClassFixture<IdCardUpstreamAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Upstream Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["ProviderJwt:Authority"] = "",
                ["IdCard:SigningKeySecretPrefix"] = "idcard-signing-key",
                ["IdCard:CurrentKeyVersion"] = "v1",
                ["IdCard:AcceptedKeyVersions:0"] = "v1",
                ["IdCard:DevSigningKeys:v1"] = "dev-key-v1-bytes-for-hmac-signing",
                ["IdCard:Qr:PixelsPerModule"] = "4",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("IdCardDefault").ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient(IdCardAdapterFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    /// <summary>Stands in for every CHO service idcard-service calls.</summary>
    public sealed class Upstream : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Sent { get; } = new();
        public HttpStatusCode TenantServiceStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode CoverageStatus { get; set; } = HttpStatusCode.OK;

        public void Reset()
        {
            Sent.Clear();
            TenantServiceStatus = HttpStatusCode.OK;
            CoverageStatus = HttpStatusCode.OK;
        }

        public IEnumerable<HttpRequestMessage> To(string host) =>
            Sent.Select(s => s.Request).Where(r => r.RequestUri!.Host.StartsWith(host, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Sent.Enqueue((request, body));
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;

            return host switch
            {
                _ when host.StartsWith("tenant-service") => Respond(TenantServiceStatus, new { tenantId = "t", configuration = new { } }),
                _ when host.StartsWith("member-service") => Respond(HttpStatusCode.OK, new { memberId = "mem-1", firstName = "Jane", lastName = "Doe" }),
                _ when host.StartsWith("coverage-service") => Respond(CoverageStatus, new[] { new { id = "cov-1", groupNumber = "G-1", planId = "P-1", status = 1 } }),
                _ when host.StartsWith("sponsor-service") => Respond(HttpStatusCode.OK, new { groupNumber = "G-1", employerName = "Acme" }),
                _ when host.StartsWith("benefit-plan-service") => Respond(HttpStatusCode.OK, new { planId = "P-1", planName = "Gold" }),
                _ when host.StartsWith("member-document-service") => Respond(HttpStatusCode.OK, new { id = "doc-" + Guid.NewGuid().ToString("N") }),
                _ when host.StartsWith("eligibility-service") => Respond(HttpStatusCode.OK, new { active = true }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent(path) }
            };
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body) };
    }

    private const string Tenant = "tenant-1";
    private const string User = "idcard-user-7";
    private readonly Factory _factory;

    public IdCardUpstreamAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Upstream.Reset();
    }

    private static JwtSecurityToken Bearer(HttpRequestMessage request)
    {
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        return new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization.Parameter);
    }

    private static string? Claim(JwtSecurityToken token, string type) =>
        token.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    private async Task SeedGlobalTemplateAsync(string tenant)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdCardTemplateRepository>()
            .UpsertAsync(TestFixtures.GlobalDefault(tenant));
    }

    // ── tenant-service (IdCardAdapterFactory) ───────────────────────────

    [Fact]
    public async Task TenantServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        // Outside a request (no caller to forward), the factory client must
        // mint a service token for the tenant. Before, the call named no
        // tenant, carried no token, got 401 and fell back to "cho".
        using var scope = _factory.Services.CreateScope();
        var adapters = scope.ServiceProvider.GetRequiredService<IdCardAdapterFactory>();

        var adapter = await adapters.GetAdapterAsync(Tenant);

        Assert.Equal("cho", adapter.Platform);
        var sent = Assert.Single(_factory.Upstream.To("tenant-service"));
        Assert.Equal($"/api/v1/tenants/{Tenant}", sent.RequestUri!.AbsolutePath);
        Assert.Equal(Tenant, sent.Headers.GetValues("X-Tenant-ID").Single());
        var token = Bearer(sent);
        Assert.Equal(Tenant, Claim(token, ChoClaimTypes.TenantId));
        Assert.Equal("idcard-service", token.Subject);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TenantServiceRefusal_IsNotSilentlyTheDefaultPlatform(HttpStatusCode refusal)
    {
        _factory.Upstream.TenantServiceStatus = refusal;
        using var scope = _factory.Services.CreateScope();
        var adapters = scope.ServiceProvider.GetRequiredService<IdCardAdapterFactory>();

        var first = await Assert.ThrowsAsync<TenantPlatformUnavailableException>(() => adapters.GetAdapterAsync(Tenant));
        Assert.Equal(refusal, first.Status);

        // Nothing was cached: the next lookup asks tenant-service again.
        await Assert.ThrowsAsync<TenantPlatformUnavailableException>(() => adapters.GetAdapterAsync(Tenant));
        Assert.Equal(2, _factory.Upstream.To("tenant-service").Count());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TenantServiceWithoutAnAnswer_UsesChoForThatCallOnly(HttpStatusCode status)
    {
        // Shared rule (TenantPlatformLookup): no answer is not cached.
        _factory.Upstream.TenantServiceStatus = status;
        using var scope = _factory.Services.CreateScope();
        var adapters = scope.ServiceProvider.GetRequiredService<IdCardAdapterFactory>();

        Assert.Equal("cho", (await adapters.GetAdapterAsync("tenant-no-answer")).Platform);
        Assert.Equal("cho", (await adapters.GetAdapterAsync("tenant-no-answer")).Platform);

        Assert.Equal(2, _factory.Upstream.To("tenant-service").Count());
    }

    [Fact]
    public async Task Order_WhenTenantServiceRefuses_FailsInsteadOfIssuingOnDefaultPlatform()
    {
        _factory.Upstream.TenantServiceStatus = HttpStatusCode.Unauthorized;
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/orders", new { memberId = "mem-1", channel = "Digital" });

        var order = await response.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.Equal("Failed", order!.Status);
        Assert.Equal("TENANT_CONFIG_UNAVAILABLE", order.FailureCode);
        Assert.Null(order.CardId);
        Assert.Empty(_factory.Upstream.To("member-service"));
        Assert.Empty(_factory.Upstream.To("member-document-service"));
    }

    // ── order path: the caller's token travels with every call ──────────

    [Fact]
    public async Task Order_EveryUpstreamCall_ForwardsCallerTokenAndRecordsCallerAsUploader()
    {
        await SeedGlobalTemplateAsync(Tenant);
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, ChoRolePermissions.EnrollmentSpecialist));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/orders",
            new { memberId = "mem-1", channel = "Digital", requestedBy = "someone-else" });

        var order = await response.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.Equal("Issued", order!.Status);

        var hosts = new[] { "tenant-service", "member-service", "coverage-service", "sponsor-service", "benefit-plan-service", "member-document-service" };
        foreach (var host in hosts)
        {
            var calls = _factory.Upstream.To(host).ToList();
            Assert.NotEmpty(calls);
            foreach (var call in calls)
            {
                var token = Bearer(call);
                Assert.Equal(User, token.Subject);
                Assert.Equal(Tenant, Claim(token, ChoClaimTypes.TenantId));
                Assert.Equal(Tenant, call.Headers.GetValues("X-Tenant-ID").Single());
            }
        }

        // member-document-service records the uploader: the token subject.
        var uploads = _factory.Upstream.Sent.Where(s => s.Request.RequestUri!.Host.StartsWith("member-document-service")).ToList();
        Assert.All(uploads, u =>
        {
            Assert.Contains(User, u.Body);
            Assert.DoesNotContain("someone-else", u.Body);
        });

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IIdCardOrderRepository>().GetAsync(Tenant, order.OrderId);
        Assert.Equal(User, stored!.RequestedBy);
    }

    [Fact]
    public async Task Revoke_StoresTokenSubjectAsRevokedBy()
    {
        var record = new IdCardRecord { TenantId = Tenant, MemberId = "mem-1", CardId = Guid.NewGuid().ToString("N") };
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IIdCardRecordRepository>().UpsertAsync(record);
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync($"/api/v1/id-cards/{record.CardId}/revoke", new { reason = "Lost" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var after = _factory.Services.CreateScope();
        var stored = await after.ServiceProvider.GetRequiredService<IIdCardRecordRepository>().FindByCardIdAsync(Tenant, record.CardId);
        Assert.Equal(User, stored!.RevokedBy);
    }

    [Fact]
    public async Task Order_CoverageRefused_IsNotReportedAsCoverageInactive()
    {
        await SeedGlobalTemplateAsync(Tenant);
        _factory.Upstream.CoverageStatus = HttpStatusCode.Forbidden;
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/orders", new { memberId = "mem-1", channel = "Digital" });

        var order = await response.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.Equal("Failed", order!.Status);
        Assert.NotEqual("COVERAGE_NOT_ACTIVE", order.FailureCode);
    }

    // ── scan path: a provider caller has no CHO token ───────────────────

    private async Task<string> IssuedCardPayloadAsync(string tenant)
    {
        var record = new IdCardRecord
        {
            TenantId = tenant,
            MemberId = "mem-1",
            CardId = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTime.UtcNow
        };
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdCardRecordRepository>().UpsertAsync(record);
        var (_, payload, _, _) = await _factory.Services.GetRequiredService<IQrCodeService>()
            .GenerateAsync(tenant, record.MemberId, record.CardId, record.IssuedAt);
        return payload;
    }

    [Fact]
    public async Task Scan_ByProvider_UsesSignedCardTenant_AndServiceTokenUpstream()
    {
        var qr = await IssuedCardPayloadAsync(Tenant);
        // A provider (development provider scheme): no CHO token, no X-Tenant-ID.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "provider-issued-token");
        client.DefaultRequestHeaders.Add("X-Provider-Id", "npi-1234567890");

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/scan", new { qrPayload = qr });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstream = _factory.Upstream.To("coverage-service").Concat(_factory.Upstream.To("eligibility-service")).ToList();
        Assert.Equal(2, upstream.Count);
        foreach (var call in upstream)
        {
            // Never the provider's own token: CHO services act on a CHO token.
            Assert.NotEqual("provider-issued-token", call.Headers.Authorization?.Parameter);
            var token = Bearer(call);
            Assert.Equal("idcard-service", token.Subject);
            Assert.Equal(Tenant, Claim(token, ChoClaimTypes.TenantId));
        }
    }

    [Fact]
    public async Task Scan_WithChoTokenForAnotherTenant_IsRejected()
    {
        var qr = await IssuedCardPayloadAsync(Tenant);
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-2");

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/scan", new { qrPayload = qr });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Upstream.To("coverage-service"));
    }

    [Fact]
    public async Task Scan_CoverageRefused_IsNotReportedAsCoverageInactive()
    {
        var qr = await IssuedCardPayloadAsync(Tenant);
        _factory.Upstream.CoverageStatus = HttpStatusCode.Unauthorized;
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/scan", new { qrPayload = qr });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ScanErrorCodes.UpstreamUnavailable, body);
        Assert.DoesNotContain(ScanErrorCodes.CoverageInactive, body);
    }
}
